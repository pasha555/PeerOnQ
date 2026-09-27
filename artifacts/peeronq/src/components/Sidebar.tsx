import { Link, useLocation } from "wouter";
import { cn } from "@/lib/utils";
import { getAccountPortalUrl } from "@/lib/accountPortal";
import { 
  LayoutDashboard, 
  Server, 
  History, 
  FolderOpen, 
  BookUser, 
  ShieldCheck, 
  Settings2,
  Globe2,
  LogOut,
  UserRound,
  ChevronLeft,
  ChevronRight,
  type LucideIcon,
} from "lucide-react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

interface SidebarProps {
  collapsed: boolean;
  onToggle: () => void;
  className?: string;
  mobile?: boolean;
  onNavigate?: () => void;
}

interface SidebarItemData {
  label: string;
  href: string;
  icon: LucideIcon;
  external?: boolean;
}

const NAV_ITEMS: SidebarItemData[] = [
  { label: "Dashboard", href: "/desktop-preview/dashboard", icon: LayoutDashboard },
  { label: "Devices", href: "/desktop-preview/devices", icon: Server },
  { label: "Sessions", href: "/desktop-preview/sessions", icon: History },
  { label: "File Transfer", href: "/desktop-preview/file-transfer", icon: FolderOpen },
  { label: "Address Book", href: "/desktop-preview/address-book", icon: BookUser },
  { label: "Security", href: "/desktop-preview/security", icon: ShieldCheck },
  { label: "Settings", href: "/desktop-preview/settings", icon: Settings2 },
];

const BOTTOM_ITEMS: SidebarItemData[] = [
  { label: "Visit public website", href: "/", icon: Globe2 },
  { label: "Open account portal", href: getAccountPortalUrl(), icon: UserRound, external: true },
  { label: "Exit desktop preview", href: "/", icon: LogOut },
];

function SidebarItemLink({ item, className, children, onNavigate }: {
  item: SidebarItemData;
  className: string;
  children: React.ReactNode;
  onNavigate?: () => void;
}) {
  if (item.external) {
    return <a href={item.href} onClick={onNavigate} className={className}>{children}</a>;
  }
  return <Link href={item.href} onClick={onNavigate} className={className}>{children}</Link>;
}

function SidebarItem({ item, active, compact, main, onNavigate }: {
  item: SidebarItemData;
  active: boolean;
  compact: boolean;
  main: boolean;
  onNavigate?: () => void;
}) {
  const activeStyle = main
    ? "bg-sidebar-primary text-sidebar-primary-foreground font-medium"
    : "bg-sidebar-accent text-sidebar-accent-foreground";
  const className = cn(
    "flex items-center gap-3 px-3 py-2 rounded-md transition-colors",
    active ? activeStyle : "hover:bg-sidebar-accent hover:text-sidebar-accent-foreground text-sidebar-foreground/80",
    compact && "justify-center",
  );
  const content = (
    <>
      <item.icon className={cn("h-5 w-5 shrink-0", main && active ? "" : "opacity-70")} />
      {!compact && <span>{item.label}</span>}
    </>
  );
  const link = <SidebarItemLink item={item} className={className} onNavigate={onNavigate}>{content}</SidebarItemLink>;
  if (!compact) return link;
  return (
    <Tooltip delayDuration={0}>
      <TooltipTrigger asChild>{link}</TooltipTrigger>
      <TooltipContent side="right">{item.label}</TooltipContent>
    </Tooltip>
  );
}

export function Sidebar({ collapsed, onToggle, className, mobile, onNavigate }: SidebarProps) {
  const [location] = useLocation();
  const compact = collapsed && !mobile;

  return (
    <aside className={cn(
      "flex flex-col h-full bg-sidebar border-r border-sidebar-border text-sidebar-foreground transition-all duration-300",
      collapsed && !mobile ? "w-16" : "w-64",
      className
    )}>
      {/* Header */}
      <div className="h-16 flex items-center px-4 shrink-0 border-b border-sidebar-border">
        <div className="flex items-center gap-3 overflow-hidden">
          <img src="/brand/peeronq-mark.svg" alt="PeerOnQ" className="w-6 h-6 shrink-0 block dark:hidden" />
          <img src="/brand/peeronq-mark-light.svg" alt="PeerOnQ" className="w-6 h-6 shrink-0 hidden dark:block" />
          {!collapsed && (
            <img src="/brand/peeronq-wordmark.svg" alt="PeerOnQ" className="h-5 block dark:hidden" />
          )}
          {!collapsed && (
            <img src="/brand/peeronq-wordmark-light.svg" alt="PeerOnQ" className="h-5 hidden dark:block" />
          )}
        </div>
      </div>

      {/* Nav */}
      <div className="flex-1 overflow-y-auto py-4 px-2 space-y-1 scrollbar-none">
        {NAV_ITEMS.map((item) => (
          <SidebarItem key={item.label} item={item} compact={compact} main onNavigate={onNavigate}
            active={location.startsWith(item.href) || (location === "/desktop-preview" && item.href.endsWith("/dashboard"))} />
        ))}
      </div>

      {/* Bottom */}
      <div className="p-2 border-t border-sidebar-border space-y-1">
        {BOTTOM_ITEMS.map((item) => (
          <SidebarItem key={item.label} item={item} compact={compact} main={false}
            onNavigate={onNavigate} active={location === item.href} />
        ))}

        {(!collapsed || mobile) && (
          <div className="px-4 py-2 mt-2 text-xs text-sidebar-foreground/50 font-mono">
            v0.5.1
          </div>
        )}
      </div>

      {/* Collapse Toggle */}
      {!mobile && (
        <button
          onClick={onToggle}
          className="absolute -right-3 top-20 bg-sidebar border border-sidebar-border rounded-full p-1 text-sidebar-foreground hover:text-primary hover:border-primary transition-colors shadow-sm"
          aria-label="Toggle sidebar"
        >
          {collapsed ? <ChevronRight className="h-4 w-4" /> : <ChevronLeft className="h-4 w-4" />}
        </button>
      )}
    </aside>
  );
}
