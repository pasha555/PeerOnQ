import { Link } from "wouter";
import { Menu, Search, Bell, User, LayoutDashboard, Settings, LogOut } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ThemeSelector } from "./ThemeSelector";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import { 
  DropdownMenu, 
  DropdownMenuContent, 
  DropdownMenuItem, 
  DropdownMenuLabel, 
  DropdownMenuSeparator, 
  DropdownMenuTrigger 
} from "@/components/ui/dropdown-menu";
import { useToast } from "@/hooks/use-toast";

interface TopbarProps {
  onMenuClick: () => void;
  title: string;
}

export function Topbar({ onMenuClick, title }: TopbarProps) {
  const { toast } = useToast();

  const handleSoon = () => {
    toast({ title: "Coming soon", description: "This feature is planned for a future release." });
  };

  return (
    <header className="h-16 border-b bg-background flex items-center justify-between px-4 shrink-0 sticky top-0 z-20">
      <div className="flex items-center gap-4">
        <Button variant="ghost" size="icon" className="md:hidden" onClick={onMenuClick}>
          <Menu className="h-5 w-5" />
        </Button>
        <h2 className="font-semibold text-foreground hidden sm:block">{title}</h2>
      </div>

      <div className="flex items-center gap-3">
        <div className="relative hidden md:block">
          <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
          <input 
            type="search" 
            placeholder="Search... (Cmd+K)" 
            className="h-9 w-64 bg-secondary/50 border-transparent focus:border-primary focus:bg-background focus:ring-1 focus:ring-primary rounded-full pl-9 pr-4 text-sm outline-none transition-all"
            readOnly
            onClick={handleSoon}
          />
        </div>

        <div className="h-6 w-[1px] bg-border mx-1 hidden sm:block"></div>

        <div className="flex items-center gap-1.5 px-3 py-1.5 rounded-full bg-secondary/50 border text-xs font-medium text-muted-foreground mr-1">
          <span className="h-1.5 w-1.5 rounded-full bg-muted-foreground"></span>
          Not connected
        </div>

        <ThemeSelector />

        <Button variant="ghost" size="icon" className="rounded-full relative" onClick={handleSoon}>
          <Bell className="h-5 w-5 text-muted-foreground" />
          {/* <span className="absolute top-1.5 right-1.5 h-2 w-2 bg-primary rounded-full border border-background"></span> */}
        </Button>

        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" className="rounded-full">
              <Avatar className="h-8 w-8">
                <AvatarImage src="" />
                <AvatarFallback className="bg-primary/10 text-primary text-xs">U</AvatarFallback>
              </Avatar>
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="w-56">
            <DropdownMenuLabel className="font-normal">
              <div className="flex flex-col space-y-1">
                <p className="text-sm font-medium leading-none">Preview User</p>
                <p className="text-xs leading-none text-muted-foreground">user@example.com</p>
              </div>
            </DropdownMenuLabel>
            <DropdownMenuSeparator />
            <DropdownMenuItem onClick={handleSoon}>
              <User className="mr-2 h-4 w-4" />
              <span>Profile</span>
            </DropdownMenuItem>
            <DropdownMenuItem asChild>
              <Link href="/desktop-preview/dashboard" className="flex items-center w-full cursor-pointer">
                <LayoutDashboard className="mr-2 h-4 w-4" />
                <span>Dashboard</span>
              </Link>
            </DropdownMenuItem>
            <DropdownMenuItem asChild>
              <Link href="/desktop-preview/settings" className="flex items-center w-full cursor-pointer">
                <Settings className="mr-2 h-4 w-4" />
                <span>Settings</span>
              </Link>
            </DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuItem onClick={handleSoon} className="text-destructive focus:text-destructive">
              <LogOut className="mr-2 h-4 w-4" />
              <span>Log out</span>
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </header>
  );
}
