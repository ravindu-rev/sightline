import { Inject, Injectable } from '@nestjs/common';
import { OrdersClient } from '../orders/orders.client';
import { AuditLog } from './audit-log';
import { CreateReportDto } from './create-report.dto';
import { REPORT_STORE, type Report, type ReportStore } from './report-store';

@Injectable()
export class ReportsService {
  constructor(
    @Inject(REPORT_STORE) private readonly store: ReportStore,
    private readonly audit: AuditLog,
    private readonly orders: OrdersClient,
  ) {}

  list(from: string, to: string) {
    this.audit.record(`list ${from}..${to}`);
    return this.store.all();
  }

  async get(id: string) {
    const report = await this.store.find(id);
    const order = report ? await this.orders.find(report.orderId) : null;
    return { ...report, order };
  }

  create(dto: CreateReportDto): Promise<Report> {
    this.audit.record('create');
    return this.store.save({ ...dto, id: String(Date.now()), orderId: 1 });
  }
}
