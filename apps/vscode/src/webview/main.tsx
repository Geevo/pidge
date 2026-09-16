import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "@api-client/ui";
import "@api-client/ui/styles.css";
import "./theme.css";

import { vscodeBridge } from "./bridge";

const container = document.getElementById("root");
if (!container) throw new Error("missing #root");

createRoot(container).render(
  <StrictMode>
    <App bridge={vscodeBridge} />
  </StrictMode>,
);
