import type { Session } from "@/types";
import { STORAGE_KEYS } from '@/compatibility/legacyBrandStorageMigration';

interface SessionRepository {
  list(): Promise<Session[]>;
  getById(id: string): Promise<Session | null>;
  save(session: Session): Promise<void>;
  remove(id: string): Promise<void>;
}

class LocalSessionRepository implements SessionRepository {
  private getStorage(): Session[] {
    try {
      const data = window.localStorage.getItem(STORAGE_KEYS.sessions);
      return data ? JSON.parse(data) : [];
    } catch {
      return [];
    }
  }

  private setStorage(sessions: Session[]) {
    window.localStorage.setItem(STORAGE_KEYS.sessions, JSON.stringify(sessions));
  }

  async list(): Promise<Session[]> {
    return this.getStorage();
  }

  async getById(id: string): Promise<Session | null> {
    return this.getStorage().find(s => s.id === id) || null;
  }

  async save(session: Session): Promise<void> {
    const sessions = this.getStorage();
    const index = sessions.findIndex(s => s.id === session.id);
    if (index >= 0) {
      sessions[index] = session;
    } else {
      sessions.push(session);
    }
    this.setStorage(sessions);
  }

  async remove(id: string): Promise<void> {
    const sessions = this.getStorage().filter(s => s.id !== id);
    this.setStorage(sessions);
  }
}

export const sessionRepository = new LocalSessionRepository();
