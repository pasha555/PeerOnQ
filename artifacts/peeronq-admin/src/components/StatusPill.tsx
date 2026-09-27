export function StatusPill({ value }: { value: string | boolean }) {
  const text = typeof value === 'boolean' ? (value ? 'Blocked' : 'Allowed') : value;
  const normalized = text.toLowerCase();
  const tone = normalized.includes('healthy') || normalized.includes('online') || normalized.includes('success')
    || ['active', 'allowed', 'available', 'completed', 'direct', 'granted', 'passed', 'ready', 'staged', 'succeeded', 'rolled back'].includes(normalized)
    ? 'success'
    : normalized.includes('degrad') || normalized.includes('warn') || normalized.includes('reconnect') || normalized.includes('pending')
      || ['queued', 'verifying', 'preflight', 'applying', 'verifying deployment', 'rolling back'].includes(normalized)
      ? 'warning'
      : normalized.includes('fail') || normalized.includes('unavailable') || normalized.includes('blocked') || normalized.includes('critical')
        || normalized.includes('revoked') || normalized.includes('not granted')
        ? 'danger'
        : 'neutral';
  return <span className={`status-pill ${tone}`}><span aria-hidden="true" />{text}</span>;
}
