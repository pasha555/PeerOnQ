import { useState } from "react";
import { Laptop, Monitor, Smartphone, Trash2, MonitorSmartphone, Clock, Package } from "lucide-react";
import type { Device } from "@/types";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetFooter,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import { StatusBadge } from "./StatusBadge";
import { DeviceId } from "./DeviceId";
import { ConfirmDialog } from "./ConfirmDialog";
import { PermissionDialog } from "./PermissionDialog";

interface DeviceDetailDrawerProps {
  device: Device | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onRemove: (id: string) => void;
}

function deviceIcon(os: string) {
  const value = os.toLowerCase();
  if (value.includes("mac") || value.includes("windows")) return <Laptop className="h-7 w-7" />;
  if (value.includes("ios") || value.includes("android")) return <Smartphone className="h-7 w-7" />;
  return <Monitor className="h-7 w-7" />;
}

export function DeviceDetailDrawer({ device, open, onOpenChange, onRemove }: DeviceDetailDrawerProps) {
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [previewOpen, setPreviewOpen] = useState(false);

  if (!device) return null;

  const rows = [
    { label: "Operating system", value: device.os, icon: Monitor },
    { label: "App version", value: device.appVersion, icon: Package },
    {
      label: "Last seen",
      value: device.lastSeen ? new Date(device.lastSeen).toLocaleString() : "Never — no agent has reported in",
      icon: Clock,
    },
  ];

  return (
    <>
      <Sheet open={open} onOpenChange={onOpenChange}>
        <SheetContent className="w-full sm:max-w-md overflow-y-auto" data-testid="device-detail-drawer">
          <SheetHeader>
            <div className="flex items-center gap-4 mb-2">
              <div className="p-3 bg-secondary rounded-xl text-foreground">{deviceIcon(device.os)}</div>
              <div className="min-w-0">
                <SheetTitle className="truncate">{device.name}</SheetTitle>
                <SheetDescription>Saved device details</SheetDescription>
              </div>
            </div>
          </SheetHeader>

          <div className="space-y-6 py-6">
            <div className="flex items-center gap-3 flex-wrap">
              <StatusBadge status={device.status} />
              {device.isPrototypeRecord && (
                <span className="px-2 py-0.5 text-xs rounded bg-primary/10 text-primary">
                  Local prototype record
                </span>
              )}
            </div>

            <div>
              <div className="text-xs uppercase tracking-wider font-semibold text-muted-foreground mb-2">
                PeerOnQ ID
              </div>
              <DeviceId id={device.peerOnQId} className="bg-muted/50" />
            </div>

            <Separator />

            <dl className="space-y-4">
              {rows.map((row) => (
                <div key={row.label} className="flex items-start gap-3">
                  <row.icon className="h-4 w-4 mt-0.5 text-muted-foreground shrink-0" />
                  <div className="min-w-0">
                    <dt className="text-xs text-muted-foreground">{row.label}</dt>
                    <dd className="text-sm break-words">{row.value}</dd>
                  </div>
                </div>
              ))}
            </dl>

            <div className="rounded-lg border bg-warning/5 border-warning/20 p-4 text-sm text-muted-foreground">
              This is a frontend preview. Device state is not verified against a live agent, and
              connecting opens an explanatory dialog instead of starting a session.
            </div>
          </div>

          <SheetFooter className="flex-col sm:flex-col gap-2">
            <Button className="w-full" onClick={() => setPreviewOpen(true)} data-testid="button-connect-device">
              <MonitorSmartphone className="mr-2 h-4 w-4" /> Connect
            </Button>
            <Button
              variant="outline"
              className="w-full text-destructive hover:text-destructive"
              onClick={() => setConfirmOpen(true)}
              data-testid="button-remove-device"
            >
              <Trash2 className="mr-2 h-4 w-4" /> Remove device
            </Button>
          </SheetFooter>
        </SheetContent>
      </Sheet>

      <PermissionDialog
        open={previewOpen}
        onOpenChange={setPreviewOpen}
        deviceId={device.peerOnQId}
        mode="control"
      />

      <ConfirmDialog
        open={confirmOpen}
        onOpenChange={setConfirmOpen}
        title="Remove this device?"
        description={`"${device.name}" will be deleted from this browser. Local prototype records cannot be recovered.`}
        confirmLabel="Remove"
        variant="destructive"
        onConfirm={() => {
          onRemove(device.id);
          setConfirmOpen(false);
          onOpenChange(false);
        }}
      />
    </>
  );
}
