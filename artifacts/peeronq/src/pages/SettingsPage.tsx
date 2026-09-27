import { PageHeader } from "@/components/PageHeader";
import { SettingsSection } from "@/components/SettingsSection";
import { useSettings } from "@/features/settings/useSettings";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { useTheme } from "@/hooks/useTheme";
import { useState } from "react";
import { useToast } from "@/hooks/use-toast";
import type { AppSettings } from "@/types";

export function SettingsPage() {
  const { settings, updateSetting } = useSettings();
  const { theme, setTheme } = useTheme();
  const { toast } = useToast();
  
  const [clearDataOpen, setClearDataOpen] = useState(false);
  const [regenIdOpen, setRegenIdOpen] = useState(false);

  const handleClearData = () => {
    window.localStorage.clear();
    toast({ title: "Data cleared", description: "Local frontend data has been deleted." });
    setTimeout(() => window.location.reload(), 1000);
  };

  const handleRegenId = () => {
    toast({ title: "Identity regenerated", description: "Your local prototype ID has been refreshed." });
  };

  return (
    <div className="space-y-6 pb-8 max-w-4xl">
      <PageHeader 
        title="Settings" 
        description="Configure application preferences."
      />

      <Tabs defaultValue="general" className="w-full">
        <TabsList className="mb-6 h-auto p-1 bg-secondary/50 rounded-lg inline-flex flex-wrap gap-1">
          <TabsTrigger value="general" className="rounded-md">General</TabsTrigger>
          <TabsTrigger value="appearance" className="rounded-md">Appearance</TabsTrigger>
          <TabsTrigger value="connections" className="rounded-md">Connections</TabsTrigger>
          <TabsTrigger value="security" className="rounded-md">Security</TabsTrigger>
          <TabsTrigger value="privacy" className="rounded-md">Privacy</TabsTrigger>
        </TabsList>

        <TabsContent value="general" className="space-y-8 animate-in fade-in-50 duration-300">
          <SettingsSection title="System">
            <div className="border rounded-xl bg-card divide-y">
              <div className="p-4 flex items-center justify-between">
                <div>
                  <h4 className="font-medium text-sm">Launch at startup</h4>
                  <p className="text-xs text-muted-foreground">Start PeerOnQ when your computer boots.</p>
                </div>
                <Switch checked={settings.launchAtStartup} onCheckedChange={(c) => updateSetting("launchAtStartup", c)} />
              </div>
              <div className="p-4 flex items-center justify-between">
                <div>
                  <h4 className="font-medium text-sm">Start minimized</h4>
                  <p className="text-xs text-muted-foreground">Keep the window hidden on startup.</p>
                </div>
                <Switch checked={settings.startMinimized} onCheckedChange={(c) => updateSetting("startMinimized", c)} />
              </div>
              <div className="p-4 flex items-center justify-between">
                <div>
                  <h4 className="font-medium text-sm">Close to tray</h4>
                  <p className="text-xs text-muted-foreground">Keep app running in background when closing window.</p>
                </div>
                <Switch checked={settings.closeToTray} onCheckedChange={(c) => updateSetting("closeToTray", c)} />
              </div>
            </div>
          </SettingsSection>
          
          <SettingsSection title="Preferences">
            <div className="border rounded-xl bg-card divide-y">
              <div className="p-4 flex items-center justify-between gap-4">
                <div>
                  <h4 className="font-medium text-sm">Language</h4>
                </div>
                <Select value={settings.language} onValueChange={(v) => updateSetting("language", v)}>
                  <SelectTrigger className="w-[180px]">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="en-US">English (US)</SelectItem>
                    <SelectItem value="en-GB">English (UK)</SelectItem>
                    <SelectItem value="fr-FR">Français</SelectItem>
                    <SelectItem value="de-DE">Deutsch</SelectItem>
                  </SelectContent>
                </Select>
              </div>
              <div className="p-4 flex items-center justify-between gap-4">
                <div>
                  <h4 className="font-medium text-sm">Default Connection Mode</h4>
                </div>
                <Select value={settings.defaultConnectionMode} onValueChange={(v) => updateSetting("defaultConnectionMode", v as AppSettings["defaultConnectionMode"])}>
                  <SelectTrigger className="w-[180px]">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="view">View Only</SelectItem>
                    <SelectItem value="control">Full Control</SelectItem>
                    <SelectItem value="file-transfer">File Transfer</SelectItem>
                  </SelectContent>
                </Select>
              </div>
            </div>
          </SettingsSection>
        </TabsContent>

        <TabsContent value="appearance" className="space-y-8 animate-in fade-in-50 duration-300">
          <SettingsSection title="Theme">
            <div className="grid grid-cols-3 gap-4">
              {([
                { id: "light", label: "Light" },
                { id: "dark", label: "Dark" },
                { id: "system", label: "System" }
              ] satisfies { id: AppSettings["theme"]; label: string }[]).map(t => (
                <button
                  key={t.id}
                  className={`flex flex-col items-center justify-center p-4 border rounded-xl gap-3 transition-all ${theme === t.id ? 'border-primary bg-primary/5 ring-1 ring-primary/20' : 'bg-card hover:border-primary/50'}`}
                  onClick={() => {
                    setTheme(t.id);
                    updateSetting("theme", t.id);
                  }}
                >
                  <div className={`w-12 h-8 rounded-md border shadow-sm flex overflow-hidden ${t.id === 'dark' ? 'bg-zinc-950' : t.id === 'light' ? 'bg-white' : 'bg-gradient-to-r from-white to-zinc-950'}`}>
                    <div className={`w-3 h-full border-r ${t.id === 'dark' ? 'border-zinc-800 bg-zinc-900' : 'border-zinc-200 bg-zinc-100'}`} />
                  </div>
                  <span className="text-sm font-medium">{t.label}</span>
                </button>
              ))}
            </div>
          </SettingsSection>
          
          <SettingsSection title="Interface">
            <div className="border rounded-xl bg-card divide-y">
              <div className="p-4 flex items-center justify-between">
                <div>
                  <h4 className="font-medium text-sm">Compact mode</h4>
                  <p className="text-xs text-muted-foreground">Reduce spacing in lists and tables.</p>
                </div>
                <Switch checked={settings.compactMode} onCheckedChange={(c) => updateSetting("compactMode", c)} />
              </div>
              <div className="p-4 flex items-center justify-between">
                <div>
                  <h4 className="font-medium text-sm">Reduced motion</h4>
                  <p className="text-xs text-muted-foreground">Minimize interface animations.</p>
                </div>
                <Switch checked={settings.reducedMotion} onCheckedChange={(c) => updateSetting("reducedMotion", c)} />
              </div>
            </div>
          </SettingsSection>
        </TabsContent>

        <TabsContent value="connections" className="space-y-8 animate-in fade-in-50 duration-300">
          <SettingsSection title="Performance">
            <div className="border rounded-xl bg-card divide-y">
              <div className="p-4 flex items-center justify-between gap-4">
                <div>
                  <h4 className="font-medium text-sm">Connection Quality</h4>
                </div>
                <Select value={settings.quality} onValueChange={(v) => updateSetting("quality", v as AppSettings["quality"])}>
                  <SelectTrigger className="w-[180px]">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="auto">Auto (Recommended)</SelectItem>
                    <SelectItem value="balanced">Balanced</SelectItem>
                    <SelectItem value="performance">Speed / Performance</SelectItem>
                    <SelectItem value="quality">Highest Quality</SelectItem>
                  </SelectContent>
                </Select>
              </div>
              <div className="p-4 flex items-center justify-between gap-4">
                <div>
                  <h4 className="font-medium text-sm">Target Framerate</h4>
                </div>
                <Select value={settings.frameRate} onValueChange={(v) => updateSetting("frameRate", v as AppSettings["frameRate"])}>
                  <SelectTrigger className="w-[180px]">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="auto">Auto</SelectItem>
                    <SelectItem value="30">30 FPS</SelectItem>
                    <SelectItem value="60">60 FPS</SelectItem>
                  </SelectContent>
                </Select>
              </div>
              <div className="p-4 flex items-center justify-between">
                <div>
                  <h4 className="font-medium text-sm">Hardware Acceleration</h4>
                  <p className="text-xs text-muted-foreground">Use GPU for video encoding/decoding.</p>
                </div>
                <Switch checked={settings.hardwareAcceleration} onCheckedChange={(c) => updateSetting("hardwareAcceleration", c)} />
              </div>
            </div>
          </SettingsSection>
          
          <SettingsSection title="Display">
            <div className="border rounded-xl bg-card divide-y">
              <div className="p-4 flex items-center justify-between">
                <div>
                  <h4 className="font-medium text-sm">Show Remote Cursor</h4>
                  <p className="text-xs text-muted-foreground">Display the remote computer's mouse cursor.</p>
                </div>
                <Switch checked={settings.showRemoteCursor} onCheckedChange={(c) => updateSetting("showRemoteCursor", c)} />
              </div>
            </div>
          </SettingsSection>
        </TabsContent>
        
        <TabsContent value="security" className="space-y-8 animate-in fade-in-50 duration-300">
           <SettingsSection title="Identity">
             <div className="border rounded-xl bg-card p-4 space-y-4">
               <div>
                  <h4 className="font-medium text-sm mb-1">Local Identity</h4>
                  <p className="text-xs text-muted-foreground mb-4">Your PeerOnQ ID is generated cryptographically. Regenerating it will invalidate all existing trusted relationships.</p>
               </div>
               <Button variant="outline" onClick={() => setRegenIdOpen(true)}>Regenerate local identity</Button>
             </div>
           </SettingsSection>
           
           <p className="text-sm text-muted-foreground p-4 bg-muted/30 rounded-lg border">
             Note: Core security settings (Approval rules, Unattended Access) are configured in the <a href="/desktop-preview/security" className="text-primary hover:underline font-medium">Security Center</a>.
           </p>
        </TabsContent>
        
        <TabsContent value="privacy" className="space-y-8 animate-in fade-in-50 duration-300">
          <SettingsSection title="Data Collection">
            <div className="border rounded-xl bg-card divide-y">
              <div className="p-4 flex items-center justify-between gap-4">
                <div>
                  <h4 className="font-medium text-sm">Diagnostic Level</h4>
                  <p className="text-xs text-muted-foreground">Amount of data sent for troubleshooting.</p>
                </div>
                <Select value={settings.diagnosticLevel} onValueChange={(v) => updateSetting("diagnosticLevel", v as AppSettings["diagnosticLevel"])}>
                  <SelectTrigger className="w-[180px]">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="none">None</SelectItem>
                    <SelectItem value="basic">Basic (Errors only)</SelectItem>
                    <SelectItem value="full">Full Logs</SelectItem>
                  </SelectContent>
                </Select>
              </div>
              <div className="p-4 flex items-center justify-between">
                <div>
                  <h4 className="font-medium text-sm">Crash Reports</h4>
                  <p className="text-xs text-muted-foreground">Automatically send crash reports to developers.</p>
                </div>
                <Switch checked={settings.crashReports} onCheckedChange={(c) => updateSetting("crashReports", c)} />
              </div>
              <div className="p-4 flex items-center justify-between bg-muted/10">
                <div>
                  <div className="flex items-center gap-2 mb-1">
                    <h4 className="font-medium text-sm">Usage Analytics</h4>
                    <span className="text-[10px] font-bold px-1.5 py-0.5 rounded bg-secondary text-secondary-foreground uppercase">Disabled by default</span>
                  </div>
                  <p className="text-xs text-muted-foreground">Help improve PeerOnQ by sending anonymous usage data.</p>
                </div>
                <Switch checked={settings.analytics} onCheckedChange={(c) => updateSetting("analytics", c)} />
              </div>
            </div>
          </SettingsSection>
          
          <SettingsSection title="Local Data">
             <div className="border rounded-xl bg-card p-4 space-y-4">
               <div>
                  <h4 className="font-medium text-sm mb-1 text-destructive">Clear Application Data</h4>
                  <p className="text-xs text-muted-foreground mb-4">Erase all local settings, devices, and session history from this browser.</p>
               </div>
               <Button variant="destructive" onClick={() => setClearDataOpen(true)}>Clear local frontend data</Button>
             </div>
           </SettingsSection>
        </TabsContent>
      </Tabs>

      <ConfirmDialog 
        open={regenIdOpen}
        onOpenChange={setRegenIdOpen}
        title="Regenerate Identity?"
        description="This will assign you a new PeerOnQ ID. All devices that trust your current ID will need to approve you again."
        onConfirm={handleRegenId}
      />
      
      <ConfirmDialog 
        open={clearDataOpen}
        onOpenChange={setClearDataOpen}
        title="Clear All Data?"
        description="This will permanently delete all locally stored devices, contacts, session history, and settings from your browser. This cannot be undone."
        confirmLabel="Clear Data"
        variant="destructive"
        onConfirm={handleClearData}
      />
    </div>
  );
}
