import { Module } from '@nestjs/common';
import { OrdersClient } from '../orders/orders.client';
import { AuditLog } from './audit-log';
import { InMemoryReportStore, REPORT_STORE } from './report-store';
import { ReportsController } from './reports.controller';
import { ReportsService } from './reports.service';

@Module({
  controllers: [ReportsController],
  providers: [
    ReportsService,
    AuditLog,
    OrdersClient,
    { provide: REPORT_STORE, useClass: InMemoryReportStore },
  ],
})
export class ReportsModule {}
