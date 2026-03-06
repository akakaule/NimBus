export class AuthClient {
  constructor(
    private apiUrl?: string,
    private accessToken?: string,
  ) {}

  getBaseUrl(
    defaultUrl: string | undefined,
    requestedUrl?: string | undefined,
  ): string {
    return requestedUrl ? requestedUrl : (this.apiUrl ?? "");
  }

  transformHttpRequestOptions(options: RequestInit): Promise<RequestInit> {
    if (options.headers && this.accessToken) {
      options.headers = {
        ...options.headers,
        Authorization: "Bearer " + this.accessToken,
      };
    }

    return Promise.resolve(options);
  }
}

export const CookieAuth = () =>
  new AuthClient(
    `https://${window.location.hostname}:${window.location.port ?? ""}`,
    undefined,
  );

export class ApiClientBase {
  constructor(private authClient: AuthClient) {}

  getBaseUrl(_: string | undefined, defaultUrl: string | undefined): string {
    return this.authClient
      ? this.authClient.getBaseUrl(defaultUrl)
      : defaultUrl!;
  }

  transformOptions(options: RequestInit): Promise<RequestInit> {
    if (!options.headers) {
      options.headers = {};
    }
    options.headers = { ...options.headers, "api-version": "2" };
    const isIE = navigator.userAgent.indexOf("Trident/") !== -1;
    if (
      isIE &&
      (options.method === undefined ||
        options.method === "GET" ||
        options.method === "HEAD")
    ) {
      options.cache = "no-cache";
      options.headers = {
        ...options.headers,
        "X-IE-Cache-Bust": new Date().getTime().toString(),
      };
    }
    return this.authClient
      ? this.authClient.transformHttpRequestOptions(options)
      : Promise.resolve(options);
  }
}
