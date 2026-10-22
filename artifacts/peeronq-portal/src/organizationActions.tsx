import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError, post, put, remove } from './api';
import { useAuth } from './auth';
import { ConfirmAction, ErrorState, LoadingState, Notice } from './components';
import { useOrganization } from './shell';
import type { Member, Organization, Team } from './types';

function errorMessage(cause: unknown) {
  return cause instanceof ApiError ? cause.message : 'The change could not be completed. Please try again.';
}

function useMembers(path: string) {
  const [state, setState] = useState<{ path: string; data?: Member[]; error?: string }>({ path });
  const [version, setVersion] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    setState({ path });
    void api<Member[]>(path, { signal: controller.signal }).then((data) => {
      if (!controller.signal.aborted) setState({ path, data });
    }).catch((cause: unknown) => {
      if (!controller.signal.aborted) setState({ path, error: errorMessage(cause) });
    });
    return () => controller.abort();
  }, [path, version]);
  return { ...(state.path === path ? state : { path }), reload: () => setVersion((value) => value + 1) };
}

export function MemberActions({ member, onChanged }: { member: Member; onChanged(): void }) {
  const { selected, reload } = useOrganization();
  const [role, setRole] = useState(member.role);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const canManage = selected?.role === 'Owner' || selected?.role === 'Administrator';
  if (!selected || !canManage || member.role === 'Owner') return null;
  const changed = async () => { onChanged(); await reload(); };
  const save = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); setBusy(true); setError(null);
    try { await put(`/organizations/${selected.id}/members/${member.accountId}/role`, { role }); await changed(); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  };
  return <div>
    {error ? <Notice tone="danger">{error}</Notice> : null}
    <form className="form-grid" onSubmit={(event) => void save(event)} aria-busy={busy}>
      <label>Role for {member.displayName}<select value={role} onChange={(event) => setRole(event.target.value as Member['role'])} disabled={busy}>
        {(['Member', 'Technician', 'Auditor', ...(selected.role === 'Owner' || member.role === 'Administrator' ? ['Administrator'] : [])] as const).map((value) => <option key={value}>{value}</option>)}
      </select></label>
      <button className="button secondary" disabled={busy || role === member.role}>{busy ? 'Saving…' : 'Save role'}</button>
    </form>
    <ConfirmAction label={`Remove ${member.displayName}`} title="Remove organization member?" description={`${member.displayName} will lose membership and team access in this organization.`} confirmLabel="Confirm member removal" onConfirm={async () => { await remove(`/organizations/${selected.id}/members/${member.accountId}`); await changed(); }} />
  </div>;
}

export function OrganizationLifecycle({ organization }: { organization: Organization }) {
  const { profile } = useAuth();
  const { reload } = useOrganization();
  const members = useMembers(`/organizations/${organization.id}/members`);
  const [nextOwner, setNextOwner] = useState('');
  if (organization.role !== 'Owner') return null;
  const candidates = members.data?.filter((member) => member.accountId !== profile?.id) ?? [];
  const recipient = candidates.find((member) => member.accountId === nextOwner);
  return <section className="panel content-section" aria-label="Organization ownership">
    <h2>Organization ownership</h2>
    <p>Transfer ownership before leaving this organization or requesting account deletion. The new owner must already be a member.</p>
    {members.error ? <ErrorState message={members.error} retry={members.reload} /> : !members.data ? <LoadingState label="Loading possible owners…" /> : candidates.length ? <div className="form-grid">
      <label>New organization owner<select value={nextOwner} onChange={(event) => setNextOwner(event.target.value)}><option value="">Choose a member</option>{candidates.map((member) => <option key={member.accountId} value={member.accountId}>{member.displayName} · {member.email}</option>)}</select></label>
      {recipient ? <ConfirmAction label="Transfer ownership" title="Transfer organization ownership?" description={`${recipient.displayName} will become the owner of ${organization.name}. You will remain an Administrator.`} confirmLabel="Confirm ownership transfer" onConfirm={async () => { await post(`/organizations/${organization.id}/transfer-ownership`, { newOwnerAccountId: recipient.accountId }); await reload(); }} /> : null}
    </div> : <p>Invite another member before transferring ownership.</p>}
    <h2>Delete organization</h2>
    <p>Remove other members and reassign or revoke managed devices first. The server checks these safeguards before accepting deletion.</p>
    <ConfirmAction label={`Delete ${organization.name}`} title="Request organization deletion?" description={`Request deletion of ${organization.name}. Existing account data and append-only security evidence follow the operator's retention procedure.`} confirmLabel="Confirm organization deletion" onConfirm={async () => { await remove(`/organizations/${organization.id}`); await reload(); }} />
  </section>;
}

export function TeamMembers({ team }: { team: Team }) {
  const { selected } = useOrganization();
  if (!selected) return null;
  return <ScopedTeamMembers key={`${selected.id}:${team.id}`} team={team} organization={selected} />;
}

function ScopedTeamMembers({ team, organization }: { team: Team; organization: Organization }) {
  const path = `/organizations/${organization.id}/teams/${team.id}/members`;
  const members = useMembers(path);
  const available = useMembers(`/organizations/${organization.id}/members`);
  const [accountId, setAccountId] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const canManage = organization.role === 'Owner' || organization.role === 'Administrator';
  const candidates = available.data?.filter((member) => !members.data?.some((current) => current.accountId === member.accountId)) ?? [];
  const add = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); if (!canManage || !candidates.some((member) => member.accountId === accountId)) return;
    setBusy(true); setError(null);
    try { await post(path, { accountId }); setAccountId(''); members.reload(); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  };
  return <div>
    <h3>Team members</h3>
    {members.error ? <ErrorState message={members.error} retry={members.reload} /> : !members.data ? <LoadingState label="Loading team members…" /> : !members.data.length ? <p>No members in this team.</p> : <ul className="guidance-list">{members.data.map((member) => <li key={member.accountId}><span>{member.displayName} · {member.email}</span>{canManage ? <ConfirmAction label={`Remove ${member.displayName} from ${team.name}`} title="Remove team member?" description={`${member.displayName} will leave ${team.name} and keep their organization membership.`} confirmLabel="Confirm team removal" onConfirm={async () => { await remove(`${path}/${member.accountId}`); members.reload(); }} /> : null}</li>)}</ul>}
    {canManage ? available.error ? <ErrorState message={available.error} retry={available.reload} /> : !available.data ? <LoadingState label="Loading organization members…" /> : <form className="form-grid" onSubmit={(event) => void add(event)} aria-busy={busy}>
      {error ? <Notice tone="danger">{error}</Notice> : null}
      <label>Add member to {team.name}<select required value={accountId} onChange={(event) => setAccountId(event.target.value)} disabled={busy || !members.data}><option value="">Choose a member</option>{candidates.map((member) => <option key={member.accountId} value={member.accountId}>{member.displayName} · {member.email}</option>)}</select></label>
      <button className="button secondary" disabled={busy || !accountId || !members.data}>{busy ? 'Adding…' : 'Add team member'}</button>
    </form> : null}
  </div>;
}
