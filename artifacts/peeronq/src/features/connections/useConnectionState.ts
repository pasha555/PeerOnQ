import type { Session } from '@/types';

/**
 * Connection state for the prototype.
 *
 * There is no desktop agent and no websocket, so there is never an active
 * session. The real implementation will subscribe to agent events and replace
 * the constant below; the shape is kept stable so callers do not change.
 */
export function useConnectionState(): { activeSession: Session | null } {
  return { activeSession: null };
}
