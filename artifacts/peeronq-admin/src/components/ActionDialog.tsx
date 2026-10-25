import { AlertTriangle, LoaderCircle, X } from 'lucide-react';
import { useEffect, useId, useRef, type FormEvent, type ReactNode } from 'react';
import { ApiError } from '../services/apiClient';

interface DialogFrameProps {
  title: string;
  description: string;
  children: ReactNode;
  busy?: boolean;
  onClose(): void;
}

export function DialogFrame({ title, description, children, busy = false, onClose }: DialogFrameProps) {
  const dialogRef = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const descriptionId = useId();

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;
    if (typeof dialog.showModal === 'function') dialog.showModal();
    else dialog.setAttribute('open', '');
    return () => {
      if (dialog.open && typeof dialog.close === 'function') dialog.close();
    };
  }, []);

  return (
    <dialog
      ref={dialogRef}
      className="admin-dialog"
      aria-labelledby={titleId}
      aria-describedby={descriptionId}
      onCancel={(event) => {
        event.preventDefault();
        if (!busy) onClose();
      }}
      onClick={(event) => {
        if (event.currentTarget === event.target && !busy) onClose();
      }}
    >
      <div className="dialog-surface">
        <header className="dialog-header">
          <div>
            <h2 id={titleId}>{title}</h2>
            <p id={descriptionId}>{description}</p>
          </div>
          <button className="icon-button" type="button" aria-label="Close dialog" disabled={busy} onClick={onClose}>
            <X size={18} aria-hidden="true" />
          </button>
        </header>
        {children}
      </div>
    </dialog>
  );
}

interface ActionDialogProps extends DialogFrameProps {
  confirmLabel: string;
  destructive?: boolean;
  error?: ApiError | null;
  confirmDisabled?: boolean;
  onConfirm(): void;
}

export function ActionDialog({
  title,
  description,
  confirmLabel,
  destructive = false,
  busy = false,
  error,
  confirmDisabled = false,
  children,
  onClose,
  onConfirm,
}: ActionDialogProps) {
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!busy && !confirmDisabled) onConfirm();
  };

  return (
    <DialogFrame title={title} description={description} busy={busy} onClose={onClose}>
      <form className="dialog-form" onSubmit={submit}>
        <div className="dialog-body">{children}</div>
        {error ? (
          <div className="inline-error" role="alert">
            <AlertTriangle size={17} aria-hidden="true" />
            <span>{error.message}{error.errorId ? ` Error reference: ${error.errorId}.` : ''}</span>
          </div>
        ) : null}
        <footer className="dialog-actions">
          <button className="button secondary" type="button" disabled={busy} onClick={onClose}>Cancel</button>
          <button className={`button ${destructive ? 'danger' : 'primary'}`} type="submit" disabled={busy || confirmDisabled}>
            {busy ? <LoaderCircle className="spin" size={17} aria-hidden="true" /> : null}
            {confirmLabel}
          </button>
        </footer>
      </form>
    </DialogFrame>
  );
}
