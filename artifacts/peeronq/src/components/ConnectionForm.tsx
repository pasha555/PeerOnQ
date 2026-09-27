import { useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Monitor, ArrowRight, FolderOpen } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Form, FormControl, FormField, FormItem, FormMessage } from "@/components/ui/form";
import { deviceIdSchema } from "@/lib/validation";
import { PermissionDialog } from "./PermissionDialog";

const formSchema = z.object({
  deviceId: deviceIdSchema,
});

export function ConnectionForm() {
  const [dialogOpen, setDialogOpen] = useState(false);
  const [mode, setMode] = useState<"view" | "control" | "file">("control");
  const [submittedId, setSubmittedId] = useState("");

  const form = useForm<z.infer<typeof formSchema>>({
    resolver: zodResolver(formSchema),
    defaultValues: { deviceId: "" },
  });

  function onSubmit(values: z.infer<typeof formSchema>) {
    setSubmittedId(values.deviceId);
    setDialogOpen(true);
  }

  return (
    <>
      <div className="bg-card border rounded-2xl p-6 shadow-sm">
        <h2 className="text-lg font-semibold mb-4">Connect to Remote Device</h2>
        <Form {...form}>
          <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-6">
            <FormField
              control={form.control}
              name="deviceId"
              render={({ field }) => (
                <FormItem>
                  <FormControl>
                    <Input 
                      placeholder="Enter PeerOnQ ID (e.g. 123-456-789-012)"
                      className="font-device-id text-lg h-14" 
                      autoComplete="off"
                      {...field} 
                    />
                  </FormControl>
                  <FormMessage />
                </FormItem>
              )}
            />
            
            <div className="grid grid-cols-3 gap-3">
              <Button
                type="button"
                variant={mode === "view" ? "default" : "outline"}
                className="h-auto py-3 px-4 flex flex-col gap-2 items-center"
                onClick={() => setMode("view")}
              >
                <Monitor className="h-5 w-5" />
                <span className="text-xs">View Only</span>
              </Button>
              <Button
                type="button"
                variant={mode === "control" ? "default" : "outline"}
                className="h-auto py-3 px-4 flex flex-col gap-2 items-center"
                onClick={() => setMode("control")}
              >
                <Monitor className="h-5 w-5" />
                <span className="text-xs">Full Control</span>
              </Button>
              <Button
                type="button"
                variant={mode === "file" ? "default" : "outline"}
                className="h-auto py-3 px-4 flex flex-col gap-2 items-center"
                onClick={() => setMode("file")}
              >
                <FolderOpen className="h-5 w-5" />
                <span className="text-xs">File Transfer</span>
              </Button>
            </div>
            
            <Button type="submit" className="w-full h-12 text-base" size="lg">
              Connect <ArrowRight className="ml-2 h-5 w-5" />
            </Button>
          </form>
        </Form>
      </div>

      <PermissionDialog 
        open={dialogOpen} 
        onOpenChange={setDialogOpen} 
        deviceId={submittedId} 
        mode={mode === "view" ? "View Only" : mode === "control" ? "Full Control" : "File Transfer"} 
      />
    </>
  );
}
