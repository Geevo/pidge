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

export function ChevronLeftIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M10 3.5L5.5 8l4.5 4.5" />
    </Svg>
  );
}

export function ChevronRightIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M6 3.5l4.5 4.5L6 12.5" />
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

/** Two stacked panes: request above, response below. */
export function RowsIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <rect x="2.2" y="2.2" width="11.6" height="11.6" rx="1.4" />
      <path d="M2.2 8h11.6" />
    </Svg>
  );
}

/** Two side-by-side panes: request beside response. */
export function ColumnsIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <rect x="2.2" y="2.2" width="11.6" height="11.6" rx="1.4" />
      <path d="M8 2.2v11.6" />
    </Svg>
  );
}

/*
 * Sliders rather than a cog. A cog needs teeth to read as one, and teeth turn
 * to mush at fourteen pixels with this stroke — the one drawn here before was
 * a ring with eight ticks around it, which read as the sun already sitting at
 * the bottom of the same rail.
 */
export function SettingsIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M2.5 4.5H4M7 4.5h6.5" />
      <circle cx="5.5" cy="4.5" r="1.5" />
      <path d="M2.5 8h6M11.5 8h2" />
      <circle cx="10" cy="8" r="1.5" />
      <path d="M2.5 11.5h3M8.5 11.5h5" />
      <circle cx="7" cy="11.5" r="1.5" />
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

export function LockIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M4.2 7.2V5.4a3.8 3.8 0 0 1 7.6 0v1.8" />
      <rect x="2.9" y="7.2" width="10.2" height="6.4" rx="1.2" />
    </Svg>
  );
}

export function EyeIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M1.5 8s2.4-4.5 6.5-4.5S14.5 8 14.5 8 12.1 12.5 8 12.5 1.5 8 1.5 8Z" />
      <circle cx="8" cy="8" r="2" />
    </Svg>
  );
}

export function EyeOffIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M6.2 3.8A6.4 6.4 0 0 1 8 3.5c4.1 0 6.5 4.5 6.5 4.5a11 11 0 0 1-1.7 2.3M4.3 4.9A11.3 11.3 0 0 0 1.5 8s2.4 4.5 6.5 4.5a6.6 6.6 0 0 0 3.4-.9" />
      <path d="M6.6 6.6a2 2 0 0 0 2.8 2.8M2 2l12 12" />
    </Svg>
  );
}

export function ExportIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M8 2.5v7.5M5 5.3 8 2.5l3 2.8M3 9.5v3.2c0 .5.4.8.8.8h8.4c.4 0 .8-.3.8-.8V9.5" />
    </Svg>
  );
}

export function ImportIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M8 2.5V10M5 7.2 8 10l3-2.8M3 9.5v3.2c0 .5.4.8.8.8h8.4c.4 0 .8-.3.8-.8V9.5" />
    </Svg>
  );
}
