import { useState, useEffect } from "react";
import { PageHeader } from "@/components/PageHeader";
import { EmptyState } from "@/components/EmptyState";
import { SessionTable } from "@/components/SessionTable";
import { PreviewNotice } from "@/components/PreviewNotice";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { History, Download } from "lucide-react";
import { sessionRepository } from "@/features/sessions/sessionRepository";
import type { Session } from "@/types";
import { useToast } from "@/hooks/use-toast";

export function SessionsPage() {
  const [sessions, setSessions] = useState<Session[]>([]);
  const [directionFilter, setDirectionFilter] = useState("all");
  const [modeFilter, setModeFilter] = useState("all");
  const [rangeFilter, setRangeFilter] = useState("all");
  const [loadedAt, setLoadedAt] = useState(0);
  const isDemo = import.meta.env.VITE_ENABLE_DEMO_DATA === "true";
  const { toast } = useToast();

  useEffect(() => {
    async function loadSessions() {
      const data = await sessionRepository.list();
      
      if (data.length === 0 && isDemo) {
        // Generate mock data if demo mode enabled
        const demoSessions: Session[] = [
          { id: "1", remoteDeviceId: "842-192-334-001", remoteDeviceName: "Office Desktop", direction: "outgoing", mode: "control", state: "ended", startedAt: new Date(Date.now() - 3600000).toISOString(), durationMs: 4500000 },
          { id: "2", remoteDeviceId: "111-222-333-444", remoteDeviceName: "Home Server", direction: "incoming", mode: "file-transfer", state: "ended", startedAt: new Date(Date.now() - 86400000).toISOString(), durationMs: 120000 },
          { id: "3", remoteDeviceId: "999-888-777-666", remoteDeviceName: "Support Request", direction: "outgoing", mode: "view", state: "failed", startedAt: new Date(Date.now() - 172800000).toISOString(), durationMs: 0 },
          { id: "4", remoteDeviceId: "555-444-333-222", direction: "incoming", mode: "control", state: "ended", startedAt: new Date(Date.now() - 259200000).toISOString(), durationMs: 980000 }
        ];
        setSessions(demoSessions);
      } else {
        setSessions(data);
      }
      setLoadedAt(Date.now());
    }
    loadSessions();
  }, [isDemo]);

  const rangeWindows: Record<string, number> = {
    "24h": 24 * 60 * 60 * 1000,
    "7d": 7 * 24 * 60 * 60 * 1000,
    "30d": 30 * 24 * 60 * 60 * 1000,
  };

  const withinRange = (session: Session) => {
    const rangeMs = rangeWindows[rangeFilter];
    if (!rangeMs) return true;
    const startedAt = new Date(session.startedAt).getTime();
    // loadedAt is captured when sessions are read, so filtering stays pure.
    return !Number.isNaN(startedAt) && loadedAt - startedAt <= rangeMs;
  };

  const filteredSessions = sessions.filter(s =>
    (directionFilter === "all" || s.direction === directionFilter) &&
    (modeFilter === "all" || s.mode === modeFilter) &&
    withinRange(s)
  );

  return (
    <div className="space-y-6 pb-8">
      <PageHeader 
        title="Session History" 
        description="Audit log of all your connection activity."
        action={
          <Button variant="outline" onClick={() => toast({ title: "Coming soon", description: "Export functionality is planned." })}>
            <Download className="mr-2 h-4 w-4" /> Export CSV
          </Button>
        }
      />

      {isDemo && (
        <PreviewNotice 
          title="Demo Data Enabled" 
          description="Demo data is enabled. No records represent real activity." 
          variant="warning" 
        />
      )}

      <div className="flex flex-col sm:flex-row gap-4 bg-card p-3 rounded-xl border">
        <Select value={directionFilter} onValueChange={setDirectionFilter}>
          <SelectTrigger className="w-full sm:w-[180px]">
            <SelectValue placeholder="All Directions" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All Directions</SelectItem>
            <SelectItem value="incoming">Incoming</SelectItem>
            <SelectItem value="outgoing">Outgoing</SelectItem>
          </SelectContent>
        </Select>

        <Select value={modeFilter} onValueChange={setModeFilter}>
          <SelectTrigger className="w-full sm:w-[180px]">
            <SelectValue placeholder="All Modes" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All Modes</SelectItem>
            <SelectItem value="view">View Only</SelectItem>
            <SelectItem value="control">Full Control</SelectItem>
            <SelectItem value="file-transfer">File Transfer</SelectItem>
          </SelectContent>
        </Select>

        <Select value={rangeFilter} onValueChange={setRangeFilter}>
          <SelectTrigger className="w-full sm:w-[180px]" data-testid="select-date-range">
            <SelectValue placeholder="All Time" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All Time</SelectItem>
            <SelectItem value="24h">Last 24 hours</SelectItem>
            <SelectItem value="7d">Last 7 days</SelectItem>
            <SelectItem value="30d">Last 30 days</SelectItem>
          </SelectContent>
        </Select>
      </div>

      {sessions.length === 0 ? (
        <EmptyState 
          icon={<History className="h-12 w-12" />}
          title="No sessions recorded"
          description="Sessions will appear here after connecting to a remote device."
        />
      ) : filteredSessions.length === 0 ? (
        <EmptyState 
          title="No matches found"
          description="No sessions match your filter criteria."
        />
      ) : (
        <SessionTable sessions={filteredSessions} />
      )}
    </div>
  );
}
