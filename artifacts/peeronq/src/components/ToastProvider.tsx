import type { ReactNode } from "react";
import { Toaster } from "@/components/ui/toaster";

interface ToastProviderProps {
  children: ReactNode;
}

/**
 * App-level toast provider: renders the tree plus a single toast outlet.
 * Mount this once in App.tsx instead of placing <Toaster /> by hand, so the
 * outlet cannot be duplicated or forgotten.
 */
export function ToastProvider({ children }: ToastProviderProps) {
  return (
    <>
      {children}
      <Toaster />
    </>
  );
}
