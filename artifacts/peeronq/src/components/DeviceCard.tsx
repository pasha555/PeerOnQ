import { Monitor, Laptop, Smartphone } from "lucide-react";
import type { Device } from "@/types";
import { StatusBadge } from "./StatusBadge";
import { DeviceId } from "./DeviceId";

interface DeviceCardProps {
  device: Device;
  onClick?: () => void;
}

export function DeviceCard({ device, onClick }: DeviceCardProps) {
  const getIcon = () => {
    if (device.os.toLowerCase().includes('mac') || device.os.toLowerCase().includes('windows')) {
      return <Laptop className="h-8 w-8" />;
    }
    if (device.os.toLowerCase().includes('ios') || device.os.toLowerCase().includes('android')) {
      return <Smartphone className="h-8 w-8" />;
    }
    return <Monitor className="h-8 w-8" />;
  };

  return (
    <div 
      className="p-5 rounded-xl border bg-card hover:border-primary/30 hover:shadow-md transition-all cursor-pointer group"
      onClick={onClick}
      role="button"
      tabIndex={0}
      onKeyDown={(e) => e.key === 'Enter' && onClick && onClick()}
    >
      <div className="flex justify-between items-start mb-4">
        <div className="p-3 bg-secondary rounded-lg text-foreground group-hover:text-primary transition-colors">
          {getIcon()}
        </div>
        <StatusBadge status={device.status} />
      </div>
      
      <h3 className="font-semibold text-lg mb-1 truncate">{device.name}</h3>
      <div className="mb-4">
        <DeviceId id={device.peerOnQId} className="bg-muted/50" />
      </div>
      
      <div className="flex justify-between items-center text-xs text-muted-foreground border-t pt-3 mt-auto">
        <span className="truncate pr-2">{device.os}</span>
        {device.isPrototypeRecord && (
          <span className="px-1.5 py-0.5 rounded bg-primary/10 text-primary whitespace-nowrap">
            Local prototype
          </span>
        )}
      </div>
    </div>
  );
}
