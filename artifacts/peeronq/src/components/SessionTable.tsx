import { format } from "date-fns";
import { ArrowDownLeft, ArrowUpRight } from "lucide-react";
import type { Session } from "@/types";
import { StatusBadge } from "./StatusBadge";

interface SessionTableProps {
  sessions: Session[];
}

export function SessionTable({ sessions }: SessionTableProps) {
  if (sessions.length === 0) {
    return null;
  }

  return (
    <div className="rounded-md border bg-card overflow-hidden">
      <div className="overflow-x-auto">
        <table className="w-full text-sm text-left">
          <thead className="bg-muted/50 text-muted-foreground">
            <tr>
              <th className="px-4 py-3 font-medium">Device</th>
              <th className="px-4 py-3 font-medium">Direction</th>
              <th className="px-4 py-3 font-medium">Mode</th>
              <th className="px-4 py-3 font-medium">Started</th>
              <th className="px-4 py-3 font-medium">Duration</th>
              <th className="px-4 py-3 font-medium text-right">Status</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-border">
            {sessions.map((session) => (
              <tr key={session.id} className="hover:bg-muted/50 transition-colors">
                <td className="px-4 py-3">
                  <div className="font-medium text-foreground">{session.remoteDeviceName || "Unknown"}</div>
                  <div className="text-xs text-muted-foreground font-device-id">{session.remoteDeviceId}</div>
                </td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-1.5">
                    {session.direction === 'incoming' ? (
                      <><ArrowDownLeft className="h-4 w-4 text-primary" /> Incoming</>
                    ) : (
                      <><ArrowUpRight className="h-4 w-4 text-success" /> Outgoing</>
                    )}
                  </div>
                </td>
                <td className="px-4 py-3 capitalize">{session.mode.replace('-', ' ')}</td>
                <td className="px-4 py-3 text-muted-foreground">
                  {format(new Date(session.startedAt), "MMM d, HH:mm")}
                </td>
                <td className="px-4 py-3 text-muted-foreground">
                  {session.durationMs ? `${Math.round(session.durationMs / 60000)}m` : '-'}
                </td>
                <td className="px-4 py-3 text-right">
                  <StatusBadge status={session.state} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
