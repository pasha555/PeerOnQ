import { PreviewNotice } from "@/components/PreviewNotice";
import { ConnectionForm } from "@/components/ConnectionForm";
import { SecurityCard } from "@/components/SecurityCard";
import { DeviceId } from "@/components/DeviceId";
import { SessionTable } from "@/components/SessionTable";
import { EmptyState } from "@/components/EmptyState";
import { Button } from "@/components/ui/button";
import { Shield, Lock, Eye, CheckCircle2, History, Download, Monitor } from "lucide-react";
import type { Session } from "@/types";

const currentHour = new Date().getHours();
const greeting = currentHour < 12 ? "Good morning" : currentHour < 18 ? "Good afternoon" : "Good evening";
const demoReferenceTime = Date.now();
const demoSessions: Session[] = [
  { id: "1", remoteDeviceId: "842-192-334-001", remoteDeviceName: "Office Desktop", direction: "outgoing", mode: "control", state: "ended", startedAt: new Date(demoReferenceTime - 3600000).toISOString(), durationMs: 4500000 },
  { id: "2", remoteDeviceId: "111-222-333-444", remoteDeviceName: "Home Server", direction: "incoming", mode: "file-transfer", state: "ended", startedAt: new Date(demoReferenceTime - 86400000).toISOString(), durationMs: 120000 },
];

export function DashboardPage() {
  return (
    <div className="space-y-8 pb-8">
      <div className="flex flex-col md:flex-row justify-between items-start gap-4">
        <div>
          <h1 className="text-3xl font-bold tracking-tight mb-2">{greeting}, Preview User</h1>
          <p className="text-muted-foreground">Manage your remote devices and active connections.</p>
        </div>
        <div className="flex items-center gap-3">
          <div className="px-3 py-1.5 rounded-full bg-success/10 text-success text-sm font-medium border border-success/20">
            Prototype mode — security features planned
          </div>
          <Button asChild>
            <a href="/downloads">
              <Download className="mr-2 h-4 w-4" /> Download for Windows
            </a>
          </Button>
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 space-y-6">
          <ConnectionForm />
          
          <div className="space-y-4">
            <h2 className="text-xl font-semibold">Security Posture</h2>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <SecurityCard 
                title="End-to-end encryption" 
                description="All session data will be strongly encrypted." 
                icon={<Lock className="h-5 w-5" />} 
                planned 
              />
              <SecurityCard 
                title="Approval required" 
                description="Connections require explicit permission by default." 
                icon={<CheckCircle2 className="h-5 w-5" />} 
                planned 
              />
              <SecurityCard 
                title="Session visibility" 
                description="Clear indicators when a session is active." 
                icon={<Eye className="h-5 w-5" />} 
                planned 
              />
              <SecurityCard 
                title="Local audit history" 
                description="All connection events logged locally." 
                icon={<Shield className="h-5 w-5" />} 
                planned 
              />
            </div>
          </div>
        </div>

        <div className="space-y-6">
          <div className="bg-card border rounded-2xl p-6 shadow-sm flex flex-col items-center text-center">
            <div className="w-16 h-16 bg-secondary rounded-full flex items-center justify-center mb-4">
              <Monitor className="h-8 w-8 text-muted-foreground" />
            </div>
            <h3 className="font-semibold text-lg mb-1">My Computer (Preview)</h3>
            <div className="mb-4">
              <span className="inline-flex px-2 py-0.5 rounded text-xs font-medium bg-muted text-muted-foreground border">
                Design preview — desktop agent not connected
              </span>
            </div>
            <div className="w-full space-y-4 text-left">
              <div className="bg-secondary/50 p-4 rounded-xl">
                <div className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">Your PeerOnQ ID</div>
                <DeviceId id="000-000-000-001" className="bg-background text-base px-4 py-2 w-full justify-between shadow-sm" />
              </div>
              <div className="text-sm space-y-2">
                <div className="flex justify-between">
                  <span className="text-muted-foreground">OS</span>
                  <span className="font-medium">Windows 11 (simulated)</span>
                </div>
                <div className="flex justify-between">
                  <span className="text-muted-foreground">Version</span>
                  <span className="font-medium">0.5.1</span>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <div className="space-y-4 pt-4">
        <h2 className="text-xl font-semibold flex items-center gap-2">
          <History className="h-5 w-5 text-primary" /> Recent Sessions
        </h2>
        {import.meta.env.VITE_ENABLE_DEMO_DATA === "true" ? (
          <>
            <PreviewNotice title="Demo Data" description="Displaying simulated session data." />
            <SessionTable sessions={demoSessions} />
          </>
        ) : (
          <EmptyState 
            icon={<History className="h-10 w-10" />}
            title="No recent sessions"
            description="No real sessions are available in this frontend preview."
          />
        )}
      </div>
    </div>
  );
}
