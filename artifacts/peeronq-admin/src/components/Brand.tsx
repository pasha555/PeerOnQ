export function Brand() {
  return (
    <div className="brand" aria-label="PeerOnQ Operations">
      <svg className="brand-mark" viewBox="0 0 24 24" aria-hidden="true">
        <rect x="1" y="1" width="22" height="22" rx="5.5" className="brand-mark-surface" />
        <circle cx="11.5" cy="11.5" r="7.5" className="brand-mark-ring" />
        <path d="M14.75 14.75 19.5 19.5" className="brand-mark-link" />
        <circle cx="6.2" cy="6.2" r="2.25" className="brand-mark-node" />
        <circle cx="19.5" cy="19.5" r="2.25" className="brand-mark-node" />
      </svg>
      <span>
        <strong>PeerOnQ</strong>
        <small>Operations</small>
      </span>
    </div>
  );
}
