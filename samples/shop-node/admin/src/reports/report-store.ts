import { Injectable } from '@nestjs/common';

export const REPORT_STORE = Symbol('REPORT_STORE');

export interface Report {
  id: string;
  title: string;
  orderId: number;
}

export interface ReportStore {
  all(): Promise<Report[]>;
  find(id: string): Promise<Report | undefined>;
  save(report: Report): Promise<Report>;
}

@Injectable()
export class InMemoryReportStore implements ReportStore {
  private readonly reports = new Map<string, Report>();

  async all() {
    return [...this.reports.values()];
  }

  async find(id: string) {
    return this.reports.get(id);
  }

  async save(report: Report) {
    this.reports.set(report.id, report);
    return report;
  }
}
