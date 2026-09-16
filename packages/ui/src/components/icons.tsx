/**
 * A handful of inline SVG icons.
 *
 * Inline rather than an icon font or a package: the VS Code webview runs under
 * a CSP that allows no remote anything, and four small paths are not worth a
 * dependency. They inherit `currentColor`, so they theme themselves.
 */

interface IconProps {
  /** Pixel size; the icons are drawn on a 16-unit grid. */
  size?: number;
}

function Svg({ size = 14, children }: IconProps & { children: React.ReactNode }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 16 16"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.5}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {children}
    </svg>
  );
}

export function PlusIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M8 3.5v9M3.5 8h9" />
    </Svg>
  );
}

export function CloseIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M4 4l8 8M12 4l-8 8" />
    </Svg>
  );
}

export function HistoryIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M2.6 8a5.4 5.4 0 1 0 1.7-3.9" />
      <path d="M2.4 3v2.6H5" />
      <path d="M8 5.2V8l2 1.3" />
    </Svg>
  );
}

export function BookmarkIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M4 2.8h8v10.4L8 10.4l-4 2.8V2.8Z" />
    </Svg>
  );
}

export function TrashIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M2.8 4.3h10.4M6.4 4.3V3h3.2v1.3M4.2 4.3l.6 8.4h6.4l.6-8.4" />
    </Svg>
  );
}
