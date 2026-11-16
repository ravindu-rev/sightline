/** The parcel carrier's rate API. */
export class CarrierClient {
  constructor(private readonly baseUrl: string) {}

  async rate(weightKg: number, country: string): Promise<number> {
    const response = await fetch(`${this.baseUrl}/rates?weight=${weightKg}&to=${country}`);
    const body = (await response.json()) as { amount: number };
    return body.amount;
  }
}
