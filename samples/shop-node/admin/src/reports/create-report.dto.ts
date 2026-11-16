export class CreateReportDto {
  title!: string;
  from!: string;
  to!: string;
  includeRefunds?: boolean;
}
