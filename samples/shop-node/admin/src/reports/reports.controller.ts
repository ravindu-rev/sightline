import { Body, Controller, Get, Param, Post, Query, UseGuards } from '@nestjs/common';
import { AdminGuard } from '../auth/admin.guard';
import { CreateReportDto } from './create-report.dto';
import { ReportsService } from './reports.service';

@Controller('reports')
export class ReportsController {
  constructor(private readonly reports: ReportsService) {}

  @Get()
  list(@Query('from') from: string, @Query('to') to: string) {
    return this.reports.list(from, to);
  }

  @Get(':id')
  get(@Param('id') id: string) {
    return this.reports.get(id);
  }

  @Post()
  @UseGuards(AdminGuard)
  create(@Body() report: CreateReportDto) {
    return this.reports.create(report);
  }
}
