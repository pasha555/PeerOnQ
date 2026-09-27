import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { ShieldCheck, MonitorSmartphone } from "lucide-react";

interface PermissionDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  deviceId?: string;
  mode?: string;
}

export function PermissionDialog({ open, onOpenChange, deviceId, mode }: PermissionDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <div className="mx-auto w-12 h-12 bg-primary/10 rounded-full flex items-center justify-center mb-4">
            <MonitorSmartphone className="h-6 w-6 text-primary" />
          </div>
          <DialogTitle className="text-center text-xl">Frontend Prototype</DialogTitle>
          <DialogDescription className="text-center">
            No real connection is established
          </DialogDescription>
        </DialogHeader>
        
        <div className="py-4 space-y-4">
          <p className="text-sm text-center">
            You attempted to connect to <strong className="font-device-id">{deviceId || "Unknown"}</strong> in <strong>{mode}</strong> mode.
          </p>
          <div className="bg-secondary/50 rounded-lg p-4 space-y-3">
            <h4 className="text-sm font-semibold flex items-center gap-2">
              <ShieldCheck className="h-4 w-4 text-primary" /> What the real app will do:
            </h4>
            <ul className="text-sm space-y-2 text-muted-foreground list-disc pl-5">
              <li>Establish an end-to-end encrypted connection</li>
              <li>Prompt the remote user for explicit permission</li>
              <li>Display a visible session indicator on both screens</li>
              <li>Record the session in the local audit log</li>
            </ul>
          </div>
        </div>

        <DialogFooter className="sm:justify-center">
          <Button variant="default" onClick={() => onOpenChange(false)}>
            Understood
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
