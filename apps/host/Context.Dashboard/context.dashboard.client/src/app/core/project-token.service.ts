import { Injectable } from '@angular/core';

/**
 * Per-project bearer tokens (Phase 3 gave every project its own token — see ProjectEndpoints.cs's
 * CreateAsync — there is no longer one host-wide token to bootstrap). A user gets a browser into a
 * project by opening a deep link of the form `http://<host>:<port>/projects/<id>?projectId=<id>&token=<token>`
 * (built from ProjectEndpoints.CreateAsync's one-time response, or from `context claude install`) —
 * this service captures that token on first load, stashes it in localStorage keyed by project
 * id, and strips both query params from the visible URL so the token doesn't linger in the address
 * bar/history. Every later request for that project reads the stashed value; the query string is
 * gone by then.
 * <para/>
 * Deliberately localStorage, not sessionStorage: sessionStorage is scoped to the tab and clears on
 * every tab/browser close, which made ProjectContextService.init() treat an ordinary restart as
 * "lost access" and silently reissue (rotate) the project's bearer token — invalidating the same
 * token baked into .mcp.json/hooks and causing 401s there. localStorage survives that; a token is
 * now only actually lost on a real profile/storage clear or a different browser.
 */
@Injectable({ providedIn: 'root' })
export class ProjectTokenService {
  constructor() {
    this.captureFromUrl();
  }

  /**
   * Stashes a token for a project directly, bypassing the URL round-trip -- used right after
   * `POST /api/projects` returns its one-time token, so the tab that just created the project can
   * start calling its API immediately without also having to re-open the deep link.
   */
  setToken(projectId: string, token: string): void {
    try {
      localStorage.setItem(storageKey(projectId), token);
    } catch {
      // Same as captureFromUrl -- nothing more to do if storage isn't available.
    }
  }

  getToken(projectId: string): string | null {
    if (!projectId) {
      return null;
    }
    try {
      return localStorage.getItem(storageKey(projectId));
    } catch {
      // localStorage unavailable (private browsing, disabled storage, etc.) -- treat as "no
      // token", same as a fresh browser that never opened the deep link.
      return null;
    }
  }

  private captureFromUrl(): void {
    const url = new URL(window.location.href);
    const projectId = url.searchParams.get('projectId');
    const token = url.searchParams.get('token');
    if (!projectId || !token) {
      return;
    }

    try {
      localStorage.setItem(storageKey(projectId), token);
    } catch {
      // Can't persist it -- nothing more to do; requests for this project will 401 same as if the
      // link had never been opened.
    }

    url.searchParams.delete('projectId');
    url.searchParams.delete('token');
    history.replaceState(history.state, '', url.toString());
  }
}

function storageKey(projectId: string): string {
  return `usage-token:${projectId}`;
}
