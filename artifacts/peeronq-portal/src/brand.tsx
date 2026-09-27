export const publicWebsite = 'https://peeronq.com';
export const sourceRepository = 'https://github.com/pasha555/PeerOnQ';

export function Brand({ light = false }: { light?: boolean }) {
  return <span className="brand-content">
    <img className={light ? undefined : 'brand-default'} src={`/brand/peeronq-lockup${light ? '-light' : ''}.svg`} width="146" height="30" alt="PeerOnQ" />
    {!light ? <img className="brand-on-dark" src="/brand/peeronq-lockup-light.svg" width="146" height="30" alt="PeerOnQ" /> : null}
    <span className="brand-divider" aria-hidden="true" />
    <span className="brand-label">Portal</span>
  </span>;
}
