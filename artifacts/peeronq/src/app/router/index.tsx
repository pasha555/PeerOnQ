import type { ComponentType, ReactNode } from "react";
import { Redirect, Route, Switch } from "wouter";
import { DesktopPreviewLayout } from "@/layouts/DesktopPreviewLayout";
import { PublicLayout } from "@/layouts/PublicLayout";
import { NotFoundPage } from "@/pages/NotFoundPage";
import { HomePage } from "@/pages/public/HomePage";
import { PrivacyPage } from "@/pages/public/PrivacyPage";
import { TermsPage } from "@/pages/public/TermsPage";
import { DashboardPage } from "@/pages/desktop-preview/DashboardPage";
import { DevicesPage } from "@/pages/desktop-preview/DevicesPage";
import { SessionsPage } from "@/pages/desktop-preview/SessionsPage";
import { FileTransferPage } from "@/pages/desktop-preview/FileTransferPage";
import { AddressBookPage } from "@/pages/desktop-preview/AddressBookPage";
import { DesktopSecurityPage } from "@/pages/desktop-preview/SecurityPage";
import { SettingsPage } from "@/pages/desktop-preview/SettingsPage";

function LayoutRoute({ layout: Layout, component: Component }: { layout: ComponentType<{ children: ReactNode }>; component: ComponentType }) {
  return (
    <Layout>
      <Component />
    </Layout>
  );
}

export function AppRouter() {
  return (
    <Switch>
      <Route path="/"><LayoutRoute layout={PublicLayout} component={HomePage} /></Route>
      <Route path="/features"><Redirect to="/#product" /></Route>
      <Route path="/security"><Redirect to="/#security" /></Route>
      <Route path="/downloads"><Redirect to="/#download" /></Route>
      <Route path="/about"><Redirect to="/#strategy" /></Route>
      <Route path="/help"><Redirect to="/#help" /></Route>
      <Route path="/privacy"><LayoutRoute layout={PublicLayout} component={PrivacyPage} /></Route>
      <Route path="/terms"><LayoutRoute layout={PublicLayout} component={TermsPage} /></Route>

      <Route path="/desktop-preview"><LayoutRoute layout={DesktopPreviewLayout} component={DashboardPage} /></Route>
      <Route path="/desktop-preview/dashboard"><LayoutRoute layout={DesktopPreviewLayout} component={DashboardPage} /></Route>
      <Route path="/desktop-preview/remote-access"><Redirect to="/desktop-preview/dashboard" /></Route>
      <Route path="/desktop-preview/devices"><LayoutRoute layout={DesktopPreviewLayout} component={DevicesPage} /></Route>
      <Route path="/desktop-preview/sessions"><LayoutRoute layout={DesktopPreviewLayout} component={SessionsPage} /></Route>
      <Route path="/desktop-preview/file-transfer"><LayoutRoute layout={DesktopPreviewLayout} component={FileTransferPage} /></Route>
      <Route path="/desktop-preview/address-book"><LayoutRoute layout={DesktopPreviewLayout} component={AddressBookPage} /></Route>
      <Route path="/desktop-preview/security"><LayoutRoute layout={DesktopPreviewLayout} component={DesktopSecurityPage} /></Route>
      <Route path="/desktop-preview/settings"><LayoutRoute layout={DesktopPreviewLayout} component={SettingsPage} /></Route>

      <Route path="/dashboard"><Redirect to="/desktop-preview/dashboard" /></Route>
      <Route path="/remote-access"><Redirect to="/desktop-preview/dashboard" /></Route>
      <Route path="/devices"><Redirect to="/desktop-preview/devices" /></Route>
      <Route path="/sessions"><Redirect to="/desktop-preview/sessions" /></Route>
      <Route path="/files"><Redirect to="/desktop-preview/file-transfer" /></Route>
      <Route path="/file-transfer"><Redirect to="/desktop-preview/file-transfer" /></Route>
      <Route path="/address-book"><Redirect to="/desktop-preview/address-book" /></Route>
      <Route path="/settings"><Redirect to="/desktop-preview/settings" /></Route>

      <Route path="/not-found" component={NotFoundPage} />
      <Route component={NotFoundPage} />
    </Switch>
  );
}
