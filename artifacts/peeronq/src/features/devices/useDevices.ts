import { useState, useEffect, useCallback } from 'react';
import { deviceRepository } from './deviceRepository';
import type { Device } from '@/types';

export function useDevices() {
  const [devices, setDevices] = useState<Device[]>([]);
  const [loading, setLoading] = useState(true);
  
  const fetchDevices = useCallback(async () => {
    setLoading(true);
    const data = await deviceRepository.list();
    setDevices(data);
    setLoading(false);
  }, []);

  useEffect(() => {
    let active = true;

    void deviceRepository.list().then((data) => {
      if (!active) return;
      setDevices(data);
      setLoading(false);
    });

    return () => {
      active = false;
    };
  }, []);

  const addDevice = async (device: Omit<Device, 'id' | 'status' | 'isPrototypeRecord'>) => {
    const newDevice: Device = {
      ...device,
      id: crypto.randomUUID(),
      status: 'offline', // Always mock offline for prototype unless specified
      isPrototypeRecord: true
    };
    await deviceRepository.save(newDevice);
    await fetchDevices();
    return newDevice;
  };

  const removeDevice = async (id: string) => {
    await deviceRepository.remove(id);
    await fetchDevices();
  };

  return { devices, loading, addDevice, removeDevice, refetch: fetchDevices };
}
