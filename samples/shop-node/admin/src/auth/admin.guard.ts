import { CanActivate, ExecutionContext, Injectable } from '@nestjs/common';

@Injectable()
export class AdminGuard implements CanActivate {
  canActivate(context: ExecutionContext) {
    return Boolean(context.switchToHttp().getRequest().headers['x-admin']);
  }
}
