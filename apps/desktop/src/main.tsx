import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "@api-client/ui";
import "@api-client/ui/styles.css";

import { createBridge } from "./bridge";

const container = document.getElementById("root");
if (!container) throw new Error("missing #root");

/*
 * The bridge is built before the first render because the title bar has to
 * know which desktop's buttons to draw; the boot screen in index.html covers
 * the one round trip that costs.
 */
const bridge = await createBridge();

createRoot(container).render(
  <StrictMode>
    <App bridge={bridge} />
  </StrictMode>,
);
