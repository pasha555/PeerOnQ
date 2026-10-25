import { useEffect, useRef, useState, type ReactNode } from "react";
import { Download, ExternalLink, LifeBuoy } from "lucide-react";
import { Link } from "wouter";
import { Button } from "@/components/ui/button";
import { getAccountPortalUrl } from "@/lib/accountPortal";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

interface OpenAppButtonProps {
  children?: ReactNode;
  className?: string;
}

export function OpenAppButton({ children = "Open App", className }: OpenAppButtonProps) {
  const [fallbackOpen, setFallbackOpen] = useState(false);
  const cleanupRef = useRef<(() => void) | null>(null);

  useEffect(() => () => cleanupRef.current?.(), []);

  const handleOpenApp = () => {
    cleanupRef.current?.();
    setFallbackOpen(false);

    let timeoutId = 0;
    const cleanup = () => {
      window.clearTimeout(timeoutId);
      document.removeEventListener("visibilitychange", handleVisibilityChange);
      cleanupRef.current = null;
    };
    const handleVisibilityChange = () => {
      if (document.visibilityState === "hidden") cleanup();
    };

    document.addEventListener("visibilitychange", handleVisibilityChange);
    timeoutId = window.setTimeout(() => {
      cleanup();
      if (document.visibilityState !== "hidden") setFallbackOpen(true);
    }, 1500);
    cleanupRef.current = cleanup;

    const launcher = document.createElement("a");
    launcher.href = "peeronq://open";
    launcher.style.display = "none";
    launcher.setAttribute("aria-hidden", "true");
    document.body.appendChild(launcher);
    launcher.click();
    launcher.remove();
  };

  return (
    <>
      <Button type="button" size="lg" className={className} onClick={handleOpenApp}>
        {children}
      </Button>
      <Dialog open={fallbackOpen} onOpenChange={setFallbackOpen}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader>
            <DialogTitle>PeerOnQ could not be opened</DialogTitle>
            <DialogDescription>
              Install the Windows application, review the setup guide, or continue to your web account.
            </DialogDescription>
          </DialogHeader>
          <div className="grid gap-3 pt-2">
            <Button asChild>
              <a href="/downloads"><Download className="h-4 w-4" />Download</a>
            </Button>
            <Button asChild variant="outline">
              <Link href="/help"><LifeBuoy className="h-4 w-4" />Installation help</Link>
            </Button>
            <Button asChild variant="ghost">
              <a href={getAccountPortalUrl()}><ExternalLink className="h-4 w-4" />Open Account Portal</a>
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
