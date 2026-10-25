import type { Device } from "@/types";
import { STORAGE_KEYS } from '@/compatibility/legacyBrandStorageMigration';

interface DeviceRepository {
  list(): Promise<Device[]>;
  getById(id: string): Promise<Device | null>;
  save(device: Device): Promise<void>;
  remove(id: string): Promise<void>;
}

class LocalDeviceRepository implements DeviceRepository {
  private getStorage(): Device[] {
    try {
      const data = window.localStorage.getItem(STORAGE_KEYS.devices);
      return data ? JSON.parse(data) : [];
    } catch {
      return [];
    }
  }

  private setStorage(devices: Device[]) {
    window.localStorage.setItem(STORAGE_KEYS.devices, JSON.stringify(devices));
  }

  async list(): Promise<Device[]> {
    return this.getStorage();
  }

  async getById(id: string): Promise<Device | null> {
    return this.getStorage().find(d => d.id === id) || null;
  }

  async save(device: Device): Promise<void> {
    const devices = this.getStorage();
    const index = devices.findIndex(d => d.id === device.id);
    if (index >= 0) {
      devices[index] = device;
    } else {
      devices.push(device);
    }
    this.setStorage(devices);
  }

  async remove(id: string): Promise<void> {
    const devices = this.getStorage().filter(d => d.id !== id);
    this.setStorage(devices);
  }
}

export const deviceRepository = new LocalDeviceRepository();
