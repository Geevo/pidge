//! Opening the window where it was left: same monitor, place, size, and
//! maximised or not.
//!
//! The window starts hidden (`visible: false` in `tauri.conf.json`) and is put
//! in place before it is shown, so it never appears in one spot and jumps to
//! another. The placement is kept in memory as the window moves and saved with
//! everything else; closing the window saves once more.
//!
//! Wayland lets no application read or set its own position, so there the
//! compositor picks where the window opens and only the size and the maximised
//! state come back.

use api_client_session::Session;
use api_client_storage::WindowPlacement;
use tauri::{LogicalSize, Monitor, PhysicalPosition, Runtime, WebviewWindow, Window};

/// Puts the window where it was last, then shows it. Shown regardless: a
/// placement that cannot be applied is no reason to have no window.
pub fn restore<R: Runtime>(window: &WebviewWindow<R>, placement: Option<WindowPlacement>) {
    if let Some(placement) = placement
        && let Err(err) = apply(window, &placement)
    {
        tracing::warn!(error = %err, "could not restore the window placement");
    }
    if let Err(err) = window.show() {
        tracing::warn!(error = %err, "could not show the window");
    }
}

/// Notes where the window is now. Called as it moves and resizes.
pub fn remember<R: Runtime>(window: &Window<R>, session: &Session) {
    match placement_of(window, session.window_placement()) {
        Ok(Some(placement)) => session.set_window_placement(placement),
        Ok(None) => {}
        Err(err) => tracing::debug!(error = %err, "could not read the window placement"),
    }
}

fn apply<R: Runtime>(window: &WebviewWindow<R>, placement: &WindowPlacement) -> tauri::Result<()> {
    let monitors = window.available_monitors()?;
    let screens: Vec<Screen> = monitors.iter().map(Screen::of).collect();
    let size = LogicalSize::new(placement.width, placement.height);

    if on_screen(placement, &screens) {
        // Position first: on a monitor with a different scale, the logical
        // size is then measured in that monitor's pixels.
        window.set_position(PhysicalPosition::new(placement.x, placement.y))?;
        window.set_size(size)?;
    } else {
        // The monitor has gone, or moved: keep the size, as far as the main
        // monitor has room for it, and start in the middle.
        let size = match window.primary_monitor()? {
            Some(monitor) => {
                let room = monitor.size().to_logical::<u32>(monitor.scale_factor());
                LogicalSize::new(size.width.min(room.width), size.height.min(room.height))
            }
            None => size,
        };
        window.set_size(size)?;
        window.center()?;
    }

    if placement.maximized {
        window.maximize()?;
    }
    Ok(())
}

/// `None` while minimised, when the place on screen means nothing.
fn placement_of<R: Runtime>(
    window: &Window<R>,
    last: Option<WindowPlacement>,
) -> tauri::Result<Option<WindowPlacement>> {
    if window.is_minimized()? {
        return Ok(None);
    }
    let maximized = window.is_maximized()?;
    if maximized && let Some(last) = last {
        // Keep the size and place from before, to come back to when it is
        // restored down.
        return Ok(Some(WindowPlacement {
            maximized: true,
            ..last
        }));
    }

    let size = window
        .inner_size()?
        .to_logical::<u32>(window.scale_factor()?);
    // Where there is no position to read (Wayland), the last one stands and
    // the size is still worth keeping.
    let position = window.outer_position().ok();
    let (x, y) = match (position, &last) {
        (Some(position), _) => (position.x, position.y),
        (None, Some(last)) => (last.x, last.y),
        (None, None) => (0, 0),
    };
    let monitor = window
        .current_monitor()?
        .and_then(|monitor| monitor.name().cloned());
    Ok(Some(WindowPlacement {
        monitor,
        x,
        y,
        width: size.width,
        height: size.height,
        maximized,
    }))
}

/// A monitor's name and its rectangle in desktop coordinates.
#[derive(Debug, Clone, PartialEq)]
struct Screen {
    name: Option<String>,
    x: i32,
    y: i32,
    width: u32,
    height: u32,
}

impl Screen {
    fn of(monitor: &Monitor) -> Self {
        let (position, size) = (monitor.position(), monitor.size());
        Self {
            name: monitor.name().cloned(),
            x: position.x,
            y: position.y,
            width: size.width,
            height: size.height,
        }
    }

    fn contains(&self, x: i32, y: i32) -> bool {
        let right = i64::from(self.x) + i64::from(self.width);
        let bottom = i64::from(self.y) + i64::from(self.height);
        x >= self.x && y >= self.y && i64::from(x) < right && i64::from(y) < bottom
    }
}

/// Whether the window would come back with its title bar on a monitor that is
/// still there: the one it was on, when both have a name to go by.
///
/// The point tested is a little inside the top-left corner, clear of the
/// invisible resize border Windows puts around a window, which can hang off
/// the edge of a monitor while the window itself does not.
fn on_screen(placement: &WindowPlacement, screens: &[Screen]) -> bool {
    let (x, y) = (
        placement.x.saturating_add(40),
        placement.y.saturating_add(16),
    );
    screens.iter().any(|screen| {
        let same_monitor = match (placement.monitor.as_deref(), screen.name.as_deref()) {
            (Some(was), Some(is)) => was == is,
            _ => true,
        };
        same_monitor && screen.contains(x, y)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn placement(monitor: Option<&str>, x: i32, y: i32) -> WindowPlacement {
        WindowPlacement {
            monitor: monitor.map(str::to_string),
            x,
            y,
            width: 1100,
            height: 740,
            maximized: false,
        }
    }

    fn screen(name: &str, x: i32, y: i32) -> Screen {
        Screen {
            name: Some(name.to_string()),
            x,
            y,
            width: 1920,
            height: 1080,
        }
    }

    #[test]
    fn comes_back_on_the_same_monitor() {
        let screens = [screen("DP-1", 0, 0), screen("HDMI-1", 1920, 0)];
        assert!(on_screen(&placement(Some("HDMI-1"), 2200, 100), &screens));
    }

    #[test]
    fn does_not_when_that_monitor_is_unplugged() {
        let screens = [screen("DP-1", 0, 0)];
        assert!(!on_screen(&placement(Some("HDMI-1"), 2200, 100), &screens));
    }

    #[test]
    fn does_not_when_another_monitor_now_has_that_spot() {
        // Same desktop coordinates, different monitor: the layout changed.
        let screens = [screen("DP-1", 0, 0), screen("DP-2", 1920, 0)];
        assert!(!on_screen(&placement(Some("HDMI-1"), 2200, 100), &screens));
    }

    #[test]
    fn allows_a_border_hanging_off_the_edge() {
        let screens = [screen("DP-1", 0, 0)];
        assert!(on_screen(&placement(Some("DP-1"), -7, 0), &screens));
    }

    #[test]
    fn does_not_when_the_title_bar_would_be_off_screen() {
        let screens = [screen("DP-1", 0, 0)];
        assert!(!on_screen(&placement(Some("DP-1"), 100, -200), &screens));
        assert!(!on_screen(&placement(Some("DP-1"), 1900, 100), &screens));
    }

    #[test]
    fn goes_by_position_alone_without_names() {
        let screens = [Screen {
            name: None,
            ..screen("", 0, 0)
        }];
        assert!(on_screen(&placement(Some("DP-1"), 100, 100), &screens));
        assert!(on_screen(
            &placement(None, 100, 100),
            &[screen("DP-1", 0, 0)]
        ));
    }

    #[test]
    fn copes_with_monitors_left_of_and_above_the_main_one() {
        let screens = [screen("DP-1", 0, 0), screen("DP-2", -1920, -300)];
        assert!(on_screen(&placement(Some("DP-2"), -1800, -200), &screens));
    }
}
