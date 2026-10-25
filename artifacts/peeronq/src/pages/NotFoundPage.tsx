import { Link } from "wouter";
import { Button } from "@/components/ui/button";
import { ShieldQuestion } from "lucide-react";

export function NotFoundPage() {
  return (
    <div className="min-h-[80vh] flex flex-col items-center justify-center text-center px-4">
      <div className="w-24 h-24 bg-primary/10 rounded-full flex items-center justify-center mb-6">
        <ShieldQuestion className="h-12 w-12 text-primary" />
      </div>
      <h1 className="text-5xl font-bold tracking-tight mb-4">404</h1>
      <h2 className="text-2xl font-semibold mb-6">This page doesn't exist</h2>
      <p className="text-muted-foreground max-w-md mx-auto mb-10">
        The link you followed may be broken, or the page may have been removed from this prototype.
      </p>
      <div className="flex gap-4">
        <Button asChild variant="default">
          <Link href="/">Back to public website</Link>
        </Button>
        <Button asChild variant="outline">
          <Link href="/">Back to Home</Link>
        </Button>
      </div>
    </div>
  );
}
