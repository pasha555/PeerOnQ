import { Component, type ErrorInfo, type ReactNode } from 'react';

interface State {
  failed: boolean;
}

export class ErrorBoundary extends Component<{ children: ReactNode }, State> {
  state: State = { failed: false };

  static getDerivedStateFromError(): State {
    return { failed: true };
  }

  componentDidCatch(_error: Error, _info: ErrorInfo): void {
    // The browser console is intentionally not used: error details may contain sensitive values.
  }

  render() {
    if (!this.state.failed) return this.props.children;
    return (
      <main className="fatal-state">
        <h1>PeerOnQ Operations could not render</h1>
        <p>Reload the page. If the problem continues, provide the time of failure to Operations.</p>
        <button className="button primary" type="button" onClick={() => window.location.reload()}>
          Reload application
        </button>
      </main>
    );
  }
}
