// src/GlassPanel.tsx
import { jsx } from "react/jsx-runtime";
function GlassPanel({ children, className, style }) {
  return /* @__PURE__ */ jsx("div", { className: ["krx-glass-panel", className].filter(Boolean).join(" "), style, children });
}

// src/MainButton.tsx
import { jsx as jsx2, jsxs } from "react/jsx-runtime";
function MainButton({ variant, label, disabled, title, onClick }) {
  return /* @__PURE__ */ jsxs(
    "button",
    {
      type: "button",
      className: `krx-main-button krx-main-button--${variant}`,
      disabled,
      title,
      onClick,
      children: [
        /* @__PURE__ */ jsx2("span", { className: "krx-main-button__label", children: (label ?? variant).toUpperCase() }),
        /* @__PURE__ */ jsx2("span", { className: "krx-main-button__tint" })
      ]
    }
  );
}

// src/Button.tsx
import { jsx as jsx3 } from "react/jsx-runtime";
function Button({ children, variant = "neutral", size = "md", disabled, title, onClick }) {
  const classes = ["krx-button", `krx-button--${variant}`];
  if (size === "sm") classes.push("krx-button--sm");
  return /* @__PURE__ */ jsx3("button", { type: "button", className: classes.join(" "), disabled, title, onClick, children });
}
export {
  Button,
  GlassPanel,
  MainButton
};
