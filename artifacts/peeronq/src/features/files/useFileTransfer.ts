import type { FileTransfer } from '@/types';

/**
 * File transfer queue for the prototype.
 *
 * The web prototype never reads the filesystem or uploads anything, so the
 * queue is always empty. The real implementation will stream progress from the
 * desktop agent; the shape is kept stable so callers do not change.
 */
export function useFileTransfer(): { transfers: FileTransfer[] } {
  return { transfers: [] };
}
