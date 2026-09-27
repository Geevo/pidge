//! The webview's own right-click menu, cut down to Copy and Paste.
//!
//! Left alone it offers Back, Reload, Save As, Print, Inspect Element and the
//! like, none of which mean anything in an app. The native menu is trimmed
//! rather than replaced with one drawn in the page, because a native Paste
//! reads the clipboard directly, where a script would have to ask for
//! permission. Where nothing is left, no menu opens at all.
//!
//! Debug builds keep the whole menu, for Inspect Element.

use tauri::{Runtime, WebviewWindow};

pub fn keep_copy_and_paste<R: Runtime>(window: &WebviewWindow<R>) {
    if cfg!(debug_assertions) {
        return;
    }
    if let Err(err) = window.with_webview(platform::keep_copy_and_paste) {
        tracing::warn!(error = %err, "could not trim the context menu");
    }
}

#[cfg(target_os = "linux")]
mod platform {
    use tauri::webview::PlatformWebview;
    use webkit2gtk::{ContextMenuAction, ContextMenuExt, ContextMenuItemExt, WebViewExt};

    pub fn keep_copy_and_paste(webview: PlatformWebview) {
        webview.inner().connect_context_menu(|_, menu, _, _| {
            for item in menu.items() {
                if !matches!(
                    item.stock_action(),
                    ContextMenuAction::Copy | ContextMenuAction::Paste
                ) {
                    menu.remove(&item);
                }
            }
            // True means handled: the menu is not shown.
            menu.n_items() == 0
        });
    }
}

#[cfg(windows)]
mod platform {
    use tauri::webview::PlatformWebview;
    use webview2_com::Microsoft::Web::WebView2::Win32::{
        ICoreWebView2_11, ICoreWebView2ContextMenuRequestedEventArgs,
    };
    use webview2_com::{ContextMenuRequestedEventHandler, take_pwstr};
    use windows_core::{Interface, PWSTR};

    pub fn keep_copy_and_paste(webview: PlatformWebview) {
        if let Err(err) = attach(&webview) {
            tracing::warn!(error = %err, "could not trim the context menu");
        }
    }

    fn attach(webview: &PlatformWebview) -> windows_core::Result<()> {
        // SAFETY: plain COM calls on the live controller Tauri hands over.
        unsafe {
            let core = webview
                .controller()
                .CoreWebView2()?
                .cast::<ICoreWebView2_11>()?;
            let handler =
                ContextMenuRequestedEventHandler::create(Box::new(|_, args| match args {
                    Some(args) => trim(&args),
                    None => Ok(()),
                }));
            let mut token = 0;
            core.add_ContextMenuRequested(&handler, &mut token)
        }
    }

    fn trim(args: &ICoreWebView2ContextMenuRequestedEventArgs) -> windows_core::Result<()> {
        // SAFETY: COM calls on the arguments of the event being handled.
        // Item names are WebView2's documented ones; separators are dropped
        // along with everything else.
        unsafe {
            let items = args.MenuItems()?;
            let mut count = 0;
            items.Count(&mut count)?;
            for index in (0..count).rev() {
                let mut name = PWSTR::null();
                items.GetValueAtIndex(index)?.Name(&mut name)?;
                if !matches!(take_pwstr(name).as_str(), "copy" | "paste") {
                    items.RemoveValueAtIndex(index)?;
                }
            }
            items.Count(&mut count)?;
            if count == 0 {
                args.SetHandled(true)?;
            }
            Ok(())
        }
    }
}

#[cfg(not(any(target_os = "linux", windows)))]
mod platform {
    pub fn keep_copy_and_paste(_: tauri::webview::PlatformWebview) {}
}
