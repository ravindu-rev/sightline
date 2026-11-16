import { Injectable, Scope } from '@nestjs/common';

/** One per request, so each entry knows which request it belongs to. */
@Injectable({ scope: Scope.REQUEST })
export class AuditLog {
  private readonly entries: string[] = [];

  record(action: string) {
    this.entries.push(`${new Date().toISOString()} ${action}`);
  }
}
