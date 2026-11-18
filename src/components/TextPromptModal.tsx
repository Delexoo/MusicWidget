import { useEffect, useId, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { AnimatePresence, motion } from "motion/react";

type TextPromptModalProps = {
  open: boolean;
  title: string;
  label?: string;
  placeholder?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  initialValue?: string;
  onConfirm: (value: string) => void;
  onCancel: () => void;
};

export function TextPromptModal({
  open,
  title,
  label,
  placeholder,
  confirmLabel = "Create",
  cancelLabel = "Cancel",
  initialValue = "",
  onConfirm,
  onCancel,
}: TextPromptModalProps) {
  const inputId = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const [value, setValue] = useState(initialValue);

  useEffect(() => {
    if (!open) return;
    setValue(initialValue);
    const id = window.setTimeout(() => {
      inputRef.current?.focus();
      inputRef.current?.select();
    }, 40);
    return () => window.clearTimeout(id);
  }, [open, initialValue]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        onCancel();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onCancel]);

  const submit = () => {
    const trimmed = value.trim();
    if (!trimmed) return;
    onConfirm(trimmed);
  };

  return createPortal(
    <AnimatePresence>
      {open ? (
        <motion.div
          className="app-modal-backdrop"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: 0.16 }}
          onMouseDown={(e) => {
            if (e.target === e.currentTarget) onCancel();
          }}
        >
          <motion.div
            role="dialog"
            aria-modal="true"
            aria-labelledby={`${inputId}-title`}
            className="app-modal"
            initial={{ opacity: 0, y: 10, scale: 0.96 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: 8, scale: 0.97 }}
            transition={{ type: "spring", stiffness: 420, damping: 32 }}
          >
            <div id={`${inputId}-title`} className="app-modal-title">
              {title}
            </div>
            {label ? (
              <label className="app-modal-label" htmlFor={inputId}>
                {label}
              </label>
            ) : null}
            <input
              id={inputId}
              ref={inputRef}
              className="app-modal-input"
              value={value}
              placeholder={placeholder}
              onChange={(e) => setValue(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  e.preventDefault();
                  submit();
                }
              }}
            />
            <div className="app-modal-actions">
              <button type="button" className="app-modal-btn ghost" onClick={onCancel}>
                {cancelLabel}
              </button>
              <button
                type="button"
                className="app-modal-btn primary"
                disabled={!value.trim()}
                onClick={submit}
              >
                {confirmLabel}
              </button>
            </div>
          </motion.div>
        </motion.div>
      ) : null}
    </AnimatePresence>,
    document.body,
  );
}
