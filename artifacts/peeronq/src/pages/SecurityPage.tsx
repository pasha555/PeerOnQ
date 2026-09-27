import { PageHeader } from "@/components/PageHeader";
import { SettingsSection } from "@/components/SettingsSection";
import { EmptyState } from "@/components/EmptyState";
import { SecurityCard } from "@/components/SecurityCard";
import { Switch } from "@/components/ui/switch";
import { ShieldCheck, ShieldAlert, Fingerprint, Key, CheckCircle2, Lock } from "lucide-react";
import { useSettings } from "@/features/settings/useSettings";

export function SecurityPage() {
  const { settings, updateSetting } = useSettings();

  return (
    <div className="space-y-8 pb-8 max-w-4xl">
      <PageHeader 
        title="Security Center" 
        description="Monitor and manage your PeerOnQ security posture."
      />

      {/* Security Score */}
      <div className="bg-card border rounded-2xl p-6 shadow-sm flex flex-col md:flex-row items-center gap-8">
        <div className="relative w-32 h-32 shrink-0 flex items-center justify-center">
          <svg className="w-full h-full transform -rotate-90" viewBox="0 0 100 100">
            <circle cx="50" cy="50" r="45" fill="none" stroke="currentColor" strokeWidth="8" className="text-muted opacity-20" />
            <circle cx="50" cy="50" r="45" fill="none" stroke="currentColor" strokeWidth="8" strokeDasharray="283" strokeDashoffset="79.24" className="text-primary" />
          </svg>
          <div className="absolute inset-0 flex flex-col items-center justify-center">
            <span className="text-3xl font-bold">72</span>
            <span className="text-[10px] font-semibold uppercase text-muted-foreground">Score</span>
          </div>
        </div>
        <div className="flex-1 space-y-2 text-center md:text-left">
          <h3 className="text-xl font-semibold">Good Security Posture</h3>
          <p className="text-muted-foreground text-sm">
            Your setup is secure, but you can improve it by enabling Two-Factor Authentication when it becomes available.
          </p>
          <div className="inline-block px-2 py-1 bg-secondary rounded text-xs font-medium text-foreground mt-2">
            Design Preview Value
          </div>
        </div>
      </div>

      <div className="bg-primary/10 border border-primary/20 rounded-xl p-5 flex gap-4 text-primary-foreground dark:text-primary">
        <ShieldCheck className="h-6 w-6 shrink-0 text-primary mt-0.5" />
        <div>
          <h4 className="font-semibold mb-1 text-foreground">Core Principle: No Hidden Monitoring</h4>
          <p className="text-sm opacity-90 text-foreground/80 leading-relaxed">
            PeerOnQ must never operate as hidden monitoring software. All incoming connections require explicit approval unless Unattended Access is deliberately configured with a strong password. A persistent visible indicator is shown during all active sessions.
          </p>
        </div>
      </div>

      <SettingsSection title="Authentication" description="Manage how you authenticate to PeerOnQ.">
        <div className="p-4 border rounded-xl bg-card flex items-center justify-between">
          <div className="flex gap-4 items-center">
            <div className="p-2 bg-secondary rounded-lg"><Fingerprint className="h-5 w-5 text-muted-foreground" /></div>
            <div>
              <h4 className="font-medium text-sm">Two-Factor Authentication</h4>
              <p className="text-xs text-muted-foreground">Require an additional code when logging in.</p>
            </div>
          </div>
          <div className="flex items-center gap-3">
            <span className="text-xs font-medium bg-muted px-2 py-1 rounded">Planned</span>
            <Switch disabled checked={false} />
          </div>
        </div>
      </SettingsSection>

      <SettingsSection title="Connection Security" description="Default rules for incoming connections.">
        <div className="border rounded-xl bg-card divide-y">
          <div className="p-4 flex items-center justify-between">
            <div>
              <h4 className="font-medium text-sm">Require Approval</h4>
              <p className="text-xs text-muted-foreground">Prompt for permission on every incoming connection.</p>
            </div>
            <Switch 
              checked={settings.requireApproval} 
              onCheckedChange={(c) => updateSetting("requireApproval", c)} 
            />
          </div>
          <div className="p-4 flex items-center justify-between">
            <div>
              <h4 className="font-medium text-sm">Remember Approved Devices</h4>
              <p className="text-xs text-muted-foreground">Skip prompt for previously approved trusted devices.</p>
            </div>
            <Switch 
              checked={settings.rememberApprovedDevices} 
              onCheckedChange={(c) => updateSetting("rememberApprovedDevices", c)} 
            />
          </div>
          <div className="p-4 flex flex-col gap-3 sm:flex-row sm:items-center justify-between bg-warning/5">
            <div>
              <div className="flex items-center gap-2 mb-1">
                <h4 className="font-medium text-sm">Unattended Access</h4>
                {!settings.unattendedAccessEnabled && <span className="text-[10px] font-bold px-1.5 py-0.5 rounded bg-success/20 text-success uppercase">Disabled by default</span>}
              </div>
              <p className="text-xs text-muted-foreground">Allow connections at any time using a specific password.</p>
            </div>
            <Switch 
              checked={settings.unattendedAccessEnabled} 
              onCheckedChange={(c) => updateSetting("unattendedAccessEnabled", c)} 
            />
          </div>
        </div>
      </SettingsSection>

      <SettingsSection title="Encryption Overview" description="How your data is protected in transit.">
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          <SecurityCard 
            title="End-to-End Encryption" 
            description="Connections use encrypted WebRTC transport with explicit consent and visible session state."
            icon={<Lock className="h-5 w-5" />} 
            planned
          />
          <SecurityCard 
            title="Perfect Forward Secrecy" 
            description="Session keys are ephemeral and discarded after the connection ends." 
            icon={<Key className="h-5 w-5" />} 
            planned
          />
        </div>
      </SettingsSection>

      <SettingsSection title="Access Control" description="Manage devices that can connect to this machine.">
        <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
          <div className="border rounded-xl bg-card p-0 flex flex-col h-64">
            <div className="p-3 border-b bg-muted/30 font-medium text-sm flex items-center gap-2">
              <CheckCircle2 className="h-4 w-4 text-success" /> Trusted Devices
            </div>
            <div className="flex-1 overflow-y-auto">
              <EmptyState title="No trusted devices" className="h-full border-none rounded-none" />
            </div>
          </div>
          
          <div className="border rounded-xl bg-card p-0 flex flex-col h-64">
            <div className="p-3 border-b bg-muted/30 font-medium text-sm flex items-center gap-2">
              <ShieldAlert className="h-4 w-4 text-destructive" /> Blocked Devices
            </div>
            <div className="flex-1 overflow-y-auto">
              <EmptyState title="No blocked devices" className="h-full border-none rounded-none" />
            </div>
          </div>
        </div>
      </SettingsSection>
    </div>
  );
}
