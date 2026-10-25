import { Monitor, Moon, Sun } from "lucide-react";
import { useTheme } from "@/hooks/useTheme";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

export function ThemeSelector() {
  const { theme, setTheme } = useTheme();

  return (
    <ToggleGroup
      type="single"
      value={theme}
      onValueChange={(val) => val && setTheme(val as "light" | "dark" | "system")}
      className="bg-secondary/50 p-1 rounded-full w-max"
    >
      <Tooltip>
        <TooltipTrigger asChild>
          <ToggleGroupItem value="light" aria-label="Light theme" className="h-8 w-8 rounded-full data-[state=on]:bg-background data-[state=on]:shadow-sm">
            <Sun className="h-4 w-4" />
          </ToggleGroupItem>
        </TooltipTrigger>
        <TooltipContent>Light</TooltipContent>
      </Tooltip>
      <Tooltip>
        <TooltipTrigger asChild>
          <ToggleGroupItem value="system" aria-label="System theme" className="h-8 w-8 rounded-full data-[state=on]:bg-background data-[state=on]:shadow-sm">
            <Monitor className="h-4 w-4" />
          </ToggleGroupItem>
        </TooltipTrigger>
        <TooltipContent>System</TooltipContent>
      </Tooltip>
      <Tooltip>
        <TooltipTrigger asChild>
          <ToggleGroupItem value="dark" aria-label="Dark theme" className="h-8 w-8 rounded-full data-[state=on]:bg-background data-[state=on]:shadow-sm">
            <Moon className="h-4 w-4" />
          </ToggleGroupItem>
        </TooltipTrigger>
        <TooltipContent>Dark</TooltipContent>
      </Tooltip>
    </ToggleGroup>
  );
}
