import React, { ErrorInfo, ReactNode } from "react";
import { AlertOctagon, Check, Copy, RefreshCcw } from "lucide-react";
import { Button } from "@/components/ui/button";

interface Props {
  children: ReactNode;
}

interface State {
  hasError: boolean;
  error: Error | null;
  errorId: string;
  copied: boolean;
}

export class ErrorBoundary extends React.Component<Props, State> {
  public state: State = {
    hasError: false,
    error: null,
    errorId: "",
    copied: false
  };

  public static getDerivedStateFromError(error: Error): State {
    return {
      hasError: true,
      error,
      errorId: Math.random().toString(36).substring(2, 9),
      copied: false
    };
  }

  public componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    console.error("Uncaught error:", error, errorInfo);
  }

  private handleRetry = () => {
    this.setState({ hasError: false, error: null, errorId: "", copied: false });
    window.location.reload();
  };

  // Only the generated ID is copied — never the message or stack trace.
  private handleCopyId = () => {
    navigator.clipboard.writeText(this.state.errorId).then(() => {
      this.setState({ copied: true });
      setTimeout(() => this.setState({ copied: false }), 2000);
    });
  };

  public render() {
    if (this.state.hasError) {
      return (
        <div className="min-h-screen w-full flex items-center justify-center bg-background p-6">
          <div className="max-w-md w-full p-8 bg-card border rounded-2xl shadow-lg text-center">
            <div className="w-16 h-16 bg-destructive/10 rounded-full flex items-center justify-center mx-auto mb-6">
              <AlertOctagon className="h-8 w-8 text-destructive" />
            </div>
            <h1 className="text-2xl font-bold mb-2">Something went wrong</h1>
            <p className="text-muted-foreground mb-8">
              We encountered an unexpected error. The application state could not be recovered.
            </p>
            
            {import.meta.env.DEV && this.state.error && (
              <div className="text-left bg-secondary p-4 rounded-lg mb-8 overflow-auto max-h-48 text-xs font-mono text-secondary-foreground">
                {this.state.error.message}
              </div>
            )}
            
            <div className="flex items-center justify-center gap-2 mb-6">
              <p className="text-xs text-muted-foreground font-mono">
                Error ID: {this.state.errorId}
              </p>
              <Button
                variant="ghost"
                size="icon"
                className="h-7 w-7"
                onClick={this.handleCopyId}
                aria-label="Copy error ID"
                data-testid="button-copy-error-id"
              >
                {this.state.copied
                  ? <Check className="h-3.5 w-3.5 text-success" />
                  : <Copy className="h-3.5 w-3.5" />}
              </Button>
            </div>

            <Button onClick={this.handleRetry} className="w-full" size="lg">
              <RefreshCcw className="mr-2 h-4 w-4" /> Reload Application
            </Button>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}
