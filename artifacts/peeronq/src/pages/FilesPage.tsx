import { PageHeader } from "@/components/PageHeader";
import { PreviewNotice } from "@/components/PreviewNotice";
import { EmptyState } from "@/components/EmptyState";
import { Button } from "@/components/ui/button";
import { ResizablePanelGroup, ResizablePanel, ResizableHandle } from "@/components/ui/resizable";
import { FolderOpen, ArrowRightLeft, Search } from "lucide-react";
import { Input } from "@/components/ui/input";

export function FilesPage() {
  return (
    <div className="space-y-6 pb-8 h-full flex flex-col">
      <PageHeader 
        title="File Transfer" 
        description="Transfer files between your devices."
      />

      <PreviewNotice 
        title="Design Preview"
        description="File transfer requires the PeerOnQ desktop app. This is a design preview only; no real filesystem access is possible."
      />

      <div className="flex-1 min-h-[500px] border rounded-xl overflow-hidden bg-card flex flex-col shadow-sm">
        <ResizablePanelGroup direction="horizontal">
          <ResizablePanel defaultSize={50} minSize={30}>
            <div className="h-full flex flex-col">
              <div className="p-3 border-b bg-muted/30 flex items-center justify-between">
                <h3 className="font-semibold text-sm flex items-center gap-2">
                  <MonitorIcon className="h-4 w-4" /> Local Machine
                </h3>
                <div className="flex items-center gap-2">
                  <div className="relative w-48">
                    <Search className="absolute left-2 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-muted-foreground" />
                    <Input className="h-8 pl-7 text-xs" placeholder="Search files..." />
                  </div>
                  <Button size="sm" variant="secondary" className="h-8 text-xs">
                    <FolderOpen className="mr-1.5 h-3.5 w-3.5" /> Browse
                  </Button>
                </div>
              </div>
              <div className="p-2 border-b bg-secondary/20 text-xs text-muted-foreground font-mono truncate">
                C:\Users\PreviewUser\Documents\
              </div>
              <div className="flex-1 overflow-y-auto p-2">
                <EmptyState 
                  icon={<FolderOpen className="h-8 w-8" />}
                  title="No files selected"
                  description="Use 'Browse' to select local files (simulated)."
                  className="h-full border-none bg-transparent"
                />
              </div>
            </div>
          </ResizablePanel>
          
          <ResizableHandle withHandle />
          
          <ResizablePanel defaultSize={50} minSize={30}>
            <div className="h-full flex flex-col bg-muted/10">
              <div className="p-3 border-b bg-muted/30 flex items-center justify-between">
                <h3 className="font-semibold text-sm flex items-center gap-2 text-muted-foreground">
                  <ServerIcon className="h-4 w-4" /> Remote Device
                </h3>
              </div>
              <div className="flex-1 overflow-y-auto flex items-center justify-center p-6">
                <div className="text-center space-y-4">
                  <div className="w-16 h-16 bg-secondary rounded-full flex items-center justify-center mx-auto mb-2">
                    <ArrowRightLeft className="h-6 w-6 text-muted-foreground" />
                  </div>
                  <h4 className="font-medium text-muted-foreground">Not Connected</h4>
                  <p className="text-sm text-muted-foreground max-w-xs mx-auto">
                    Connect to a remote device in File Transfer mode to browse and transfer files.
                  </p>
                </div>
              </div>
            </div>
          </ResizablePanel>
        </ResizablePanelGroup>
        
        <div className="h-48 border-t bg-muted/30 flex flex-col">
          <div className="p-2 border-b bg-card flex items-center justify-between">
            <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">Transfer Queue</h4>
            <div className="flex gap-2">
              <Button size="sm" variant="ghost" className="h-7 text-xs" disabled title="Prototype only">Pause All</Button>
              <Button size="sm" variant="ghost" className="h-7 text-xs text-destructive" disabled title="Prototype only">Cancel All</Button>
            </div>
          </div>
          <div className="flex-1 flex items-center justify-center">
            <p className="text-sm text-muted-foreground">No transfers in progress</p>
          </div>
        </div>
      </div>
    </div>
  );
}

// Icons
import { Monitor as MonitorIcon, Server as ServerIcon } from "lucide-react";
