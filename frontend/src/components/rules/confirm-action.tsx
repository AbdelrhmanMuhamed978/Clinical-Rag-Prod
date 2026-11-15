"use client";

import { useEffect, useId, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import styles from "./confirm-action.module.css";

export type Confirmation = { title: string; description: string; confirmLabel: string; onConfirm: () => void };

export function ConfirmDialog({ request, onDismiss }: { request: Confirmation | null; onDismiss: () => void }) {
  const id = useId();
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    if (request && !dialog.current?.open) dialog.current?.showModal();
    else if (!request && dialog.current?.open) { dialog.current.returnValue = ""; dialog.current.close(); }
  }, [request]);
  return <dialog ref={dialog} aria-labelledby={`${id}-title`} aria-describedby={`${id}-description`}
    className={`${styles.dialog} m-auto max-h-[85dvh] w-[min(32rem,90vw)] overflow-auto rounded-xl border bg-card p-6 text-foreground shadow-xl`}
    onClose={event => {
      const value = event.currentTarget.returnValue;
      event.currentTarget.returnValue = ""; // Esc must never reuse a previous destructive choice.
      const action = request?.onConfirm;
      onDismiss();
      if (value === "confirm") action?.();
    }}>
    <h2 id={`${id}-title`} className="text-lg font-semibold">{request?.title}</h2>
    <p id={`${id}-description`} className="my-4 text-sm">{request?.description}</p>
    <form method="dialog" aria-labelledby={`${id}-title`} className="flex flex-wrap justify-end gap-2" onSubmit={event => {
      event.preventDefault();
      event.stopPropagation();
      const submitter = (event.nativeEvent as SubmitEvent).submitter;
      dialog.current?.close(submitter?.getAttribute("value") ?? "cancel");
    }}>
      <Button type="submit" value="cancel" variant="outline" className="min-h-12" autoFocus>Cancel</Button>
      <Button type="submit" value="confirm" variant="destructive" className="min-h-12">{request?.confirmLabel}</Button>
    </form>
  </dialog>;
}

export function ConfirmAction({ label, title, description, confirmLabel = label, disabled, onConfirm }: {
  label: string; title: string; description: string; confirmLabel?: string; disabled: boolean; onConfirm: () => void;
}) {
  const [request, setRequest] = useState<Confirmation | null>(null);
  return <>
    <Button type="button" variant="outline" className="min-h-12" disabled={disabled}
      onClick={() => setRequest({ title, description, confirmLabel, onConfirm })}>{label}</Button>
    <ConfirmDialog request={request} onDismiss={() => setRequest(null)} />
  </>;
}
