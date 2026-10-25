import type { Contact, ContactGroup } from "@/types";
import { STORAGE_KEYS } from '@/compatibility/legacyBrandStorageMigration';

interface ContactRepository {
  listContacts(): Promise<Contact[]>;
  getContactById(id: string): Promise<Contact | null>;
  saveContact(contact: Contact): Promise<void>;
  removeContact(id: string): Promise<void>;
  
  listGroups(): Promise<ContactGroup[]>;
  saveGroup(group: ContactGroup): Promise<void>;
  removeGroup(id: string): Promise<void>;
}

class LocalContactRepository implements ContactRepository {
  private getContacts(): Contact[] {
    try {
      const data = window.localStorage.getItem(STORAGE_KEYS.contacts);
      return data ? JSON.parse(data) : [];
    } catch {
      return [];
    }
  }

  private setContacts(contacts: Contact[]) {
    window.localStorage.setItem(STORAGE_KEYS.contacts, JSON.stringify(contacts));
  }

  private getGroups(): ContactGroup[] {
    try {
      const data = window.localStorage.getItem(STORAGE_KEYS.groups);
      return data ? JSON.parse(data) : [];
    } catch {
      return [];
    }
  }

  private setGroups(groups: ContactGroup[]) {
    window.localStorage.setItem(STORAGE_KEYS.groups, JSON.stringify(groups));
  }

  async listContacts(): Promise<Contact[]> { return this.getContacts(); }
  async getContactById(id: string): Promise<Contact | null> { return this.getContacts().find(c => c.id === id) || null; }
  
  async saveContact(contact: Contact): Promise<void> {
    const contacts = this.getContacts();
    const index = contacts.findIndex(c => c.id === contact.id);
    if (index >= 0) {
      contacts[index] = contact;
    } else {
      contacts.push(contact);
    }
    this.setContacts(contacts);
  }

  async removeContact(id: string): Promise<void> {
    this.setContacts(this.getContacts().filter(c => c.id !== id));
  }

  async listGroups(): Promise<ContactGroup[]> { return this.getGroups(); }
  
  async saveGroup(group: ContactGroup): Promise<void> {
    const groups = this.getGroups();
    const index = groups.findIndex(g => g.id === group.id);
    if (index >= 0) {
      groups[index] = group;
    } else {
      groups.push(group);
    }
    this.setGroups(groups);
  }

  async removeGroup(id: string): Promise<void> {
    this.setGroups(this.getGroups().filter(g => g.id !== id));
  }
}

export const contactRepository = new LocalContactRepository();
