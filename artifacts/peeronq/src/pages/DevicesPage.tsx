import { useState } from "react";
import { PageHeader } from "@/components/PageHeader";
import { EmptyState } from "@/components/EmptyState";
import { DeviceCard } from "@/components/DeviceCard";
import { DeviceDetailDrawer } from "@/components/DeviceDetailDrawer";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Search, Plus, Server, LayoutGrid, List } from "lucide-react";
import { useDevices } from "@/features/devices/useDevices";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { Form, FormControl, FormField, FormItem, FormLabel, FormMessage } from "@/components/ui/form";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { deviceIdSchema, deviceAliasSchema } from "@/lib/validation";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import type { Device } from "@/types";

const addDeviceSchema = z.object({
  name: deviceAliasSchema,
  peerOnQId: deviceIdSchema,
  os: z.string().min(1, "OS is required"),
});

function DeviceResults({ devices, filteredDevices, viewMode, onAdd, onSelect }: {
  devices: Device[];
  filteredDevices: Device[];
  viewMode: "grid" | "list";
  onAdd: () => void;
  onSelect: (id: string) => void;
}) {
  if (devices.length === 0) {
    return (
      <EmptyState icon={<Server className="h-12 w-12" />} title="No devices found"
        description="Add your first device to get started. Devices are stored locally in your browser."
        action={<Button onClick={onAdd}><Plus className="mr-2 h-4 w-4" /> Add Device</Button>} />
    );
  }
  if (filteredDevices.length === 0) {
    return <EmptyState icon={<Search className="h-12 w-12" />} title="No matches found"
      description="No devices match your search or filter criteria." />;
  }
  const className = viewMode === "grid" ? "grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4" : "space-y-3";
  return (
    <div className={className}>
      {filteredDevices.map((device) => (
        <DeviceCard key={device.id} device={device} onClick={() => onSelect(device.id)} />
      ))}
    </div>
  );
}

export function DevicesPage() {
  const { devices, addDevice, removeDevice } = useDevices();
  const [search, setSearch] = useState("");
  const [statusFilter, setStatusFilter] = useState("all");
  const [viewMode, setViewMode] = useState<"grid" | "list">("grid");
  const [addDialogOpen, setDialogOpen] = useState(false);
  const [selectedDeviceId, setSelectedDeviceId] = useState<string | null>(null);

  const form = useForm<z.infer<typeof addDeviceSchema>>({
    resolver: zodResolver(addDeviceSchema),
    defaultValues: { name: "", peerOnQId: "", os: "Windows" },
  });

  const onSubmit = async (values: z.infer<typeof addDeviceSchema>) => {
    await addDevice({
      name: values.name,
      peerOnQId: values.peerOnQId,
      os: values.os,
      appVersion: ""
    });
    setDialogOpen(false);
    form.reset();
  };

  const selectedDevice = devices.find(d => d.id === selectedDeviceId) ?? null;

  const filteredDevices = devices.filter(d =>
    (statusFilter === "all" || d.status === statusFilter) &&
    (d.name.toLowerCase().includes(search.toLowerCase()) || d.peerOnQId.toLowerCase().includes(search.toLowerCase()))
  );

  return (
    <div className="space-y-6">
      <PageHeader 
        title="Devices" 
        description="Manage your trusted remote devices."
        action={
          <Dialog open={addDialogOpen} onOpenChange={setDialogOpen}>
            <DialogTrigger asChild>
              <Button>
                <Plus className="mr-2 h-4 w-4" /> Add Device
              </Button>
            </DialogTrigger>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>Add Trusted Device</DialogTitle>
                <DialogDescription>
                  Save a remote device for quick access. This is saved locally in your browser.
                </DialogDescription>
              </DialogHeader>
              <Form {...form}>
                <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-4 pt-4">
                  <FormField
                    control={form.control}
                    name="name"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>Device Name (Alias)</FormLabel>
                        <FormControl>
                          <Input placeholder="e.g. Office Desktop" {...field} />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={form.control}
                    name="peerOnQId"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>PeerOnQ ID</FormLabel>
                        <FormControl>
                          <Input placeholder="000-000-000-000" className="font-device-id" {...field} />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={form.control}
                    name="os"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>Operating System</FormLabel>
                        <Select onValueChange={field.onChange} defaultValue={field.value}>
                          <FormControl>
                            <SelectTrigger>
                              <SelectValue placeholder="Select OS" />
                            </SelectTrigger>
                          </FormControl>
                          <SelectContent>
                            <SelectItem value="Windows">Windows</SelectItem>
                            <SelectItem value="macOS">macOS</SelectItem>
                            <SelectItem value="Linux">Linux</SelectItem>
                          </SelectContent>
                        </Select>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <DialogFooter className="pt-4">
                    <Button type="submit">Save Device</Button>
                  </DialogFooter>
                </form>
              </Form>
            </DialogContent>
          </Dialog>
        }
      />

      <div className="flex flex-col sm:flex-row gap-4 justify-between items-center bg-card p-2 rounded-xl border">
        <div className="relative w-full sm:w-96">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
          <Input 
            placeholder="Search devices..." 
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="pl-9 bg-secondary/30 border-transparent focus-visible:bg-background" 
          />
        </div>
        <div className="flex items-center gap-2 w-full sm:w-auto">
          <Select value={statusFilter} onValueChange={setStatusFilter}>
            <SelectTrigger className="w-full sm:w-[140px] bg-secondary/30 border-transparent">
              <SelectValue placeholder="All Status" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All Status</SelectItem>
              <SelectItem value="online">Online</SelectItem>
              <SelectItem value="offline">Offline</SelectItem>
            </SelectContent>
          </Select>
          <div className="flex bg-secondary/50 rounded-lg p-1">
            <Button variant={viewMode === "grid" ? "secondary" : "ghost"} size="icon" className="h-8 w-8 rounded-md" onClick={() => setViewMode("grid")}>
              <LayoutGrid className="h-4 w-4" />
            </Button>
            <Button variant={viewMode === "list" ? "secondary" : "ghost"} size="icon" className="h-8 w-8 rounded-md" onClick={() => setViewMode("list")}>
              <List className="h-4 w-4" />
            </Button>
          </div>
        </div>
      </div>

      <DeviceResults devices={devices} filteredDevices={filteredDevices} viewMode={viewMode}
        onAdd={() => setDialogOpen(true)} onSelect={setSelectedDeviceId} />

      <DeviceDetailDrawer
        device={selectedDevice}
        open={selectedDeviceId !== null}
        onOpenChange={(open) => !open && setSelectedDeviceId(null)}
        onRemove={removeDevice}
      />
    </div>
  );
}
