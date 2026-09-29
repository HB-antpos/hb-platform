export type BrowserExtensionPrivacySection = {
  id: string
  title: string
  paragraphs: string[]
  items: string[]
}

export type BrowserExtensionPrivacyPolicy = {
  policyVersion: string
  language: 'en'
  title: string
  subtitle: string
  effectiveDate: string
  summary: string
  responsibleEntity: string
  privacyEmail: string
  publicUrl: string
  sections: BrowserExtensionPrivacySection[]
}

export const BROWSER_EXTENSION_PRIVACY_POLICY = {
  policyVersion: '2026-09-24',
  language: 'en',
  title: 'HB Supplier Order Browser Extension Privacy Policy',
  subtitle: 'Hot Bargain internal supplier-ordering browser extension',
  effectiveDate: '24 September 2026',
  summary:
    'This policy explains how HOT BARGAIN INTERNATIONAL PTY LTD (we, us or our) handles information when authorised employees use the HB Supplier Order browser extension. It applies only to this extension and should be read before installation or use.',
  responsibleEntity: 'HOT BARGAIN INTERNATIONAL PTY LTD',
  privacyEmail: 'inquiries@hotbargain.com.au',
  publicUrl: 'https://hotbargain.vip/privacy/browser-extension',
  sections: [
    {
      id: 'purpose-and-scope',
      title: '1. Purpose and scope',
      paragraphs: [
        'HB Supplier Order is an internal business tool for authorised Hot Bargain employees. It helps an employee review internal purchase history, sales history and supplier-level sales insights while ordering products on supported supplier product-list pages, and records how each supplier groups its products into categories so that Hot Bargain can organise the matching internal products.',
        'The extension does not offer public registration, place supplier orders automatically, alter supplier website accounts, or make purchasing decisions on behalf of a user.',
      ],
      items: [],
    },
    {
      id: 'information-we-handle',
      title: '2. Information we handle',
      paragraphs: [
        'The extension handles only the information needed to connect authorised employees, identify supplier products and categories, and retrieve the corresponding internal business records.',
      ],
      items: [
        'Account and authentication information: the employee signs in on the Hot Bargain website, not in the extension. The extension never collects, reads or stores usernames, passwords, website cookies or refresh tokens. After a one-time authorisation code exchange it receives a short-lived access token and a minimal account identity (user identifier, username and display name).',
        'Store and preference information: selected store code, display language, trusted Hot Bargain API origin, supplier-origin permission state and whether automatic category capture is switched on for each supplier.',
        'Supplier product information: the supplier domain, supplier code and item number detected from configured product-list pages. Product names or images may be read locally when needed to present the extension interface.',
        'Supplier category information: on supplier sites the employee has authorised, the category names shown in the page breadcrumb, heading and category menu, the website paths and addresses of those category pages, the item numbers listed on each category page, the page number, the capture time and whether the capture was automatic or started by the employee. It does not include prices, supplier account details, orders or form contents.',
        'Internal business information: purchase dates, purchase quantities, order references, sales quantities, average sale prices and supplier top-seller results returned for the authorised employee and selected store or permitted company scope.',
        'Technical configuration: declarative supplier profiles containing approved domains, page-address patterns, selectors, numeric capture limits and allow-listed field transformations.',
      ],
    },
    {
      id: 'how-information-is-used',
      title: '3. How we use information',
      paragraphs: [
        'We use information only to provide and secure the extension\'s single purpose.',
      ],
      items: [
        'Authenticate the employee and enforce role and store access granted by Hot Bargain systems.',
        'Match a product on a supported supplier list page with Hot Bargain internal purchase and sales records.',
        'Display item history and supplier sales insights in the browser side panel and beside the relevant supplier item.',
        'Build and maintain each supplier\'s category structure and link Hot Bargain products to the supplier category in which they are listed. Automatic capture runs only on authorised supplier category pages the employee opens; a full category capture runs only after the employee starts it from the side panel.',
        'Remember the employee\'s selected store, language, trusted API origin, granted supplier sites and category capture preference.',
        'Maintain security, diagnose faults and comply with legal or audit obligations.',
      ],
    },
    {
      id: 'browser-permissions',
      title: '4. Browser permissions',
      paragraphs: [
        'The extension requests only the browser permissions required for its stated purpose. Supplier-site access is optional and is requested one supplier origin at a time after a user action.',
      ],
      items: [
        'Storage: saves the short-lived access token (session storage only), selected settings, permission state, cached declarative supplier profiles, short-lived category capture de-duplication records and the progress and summary of the latest category capture. It does not store passwords, refresh tokens, supplier website credentials or purchase and sales history as a local archive.',
        'Side panel: provides the website-session connection status, store selection, supplier permission, supplier category capture controls, item-history and top-seller interface.',
        'Scripting and supplier host access: registers packaged content scripts on user-authorised supplier origins to detect configured item numbers, add history controls to product-list pages and read category breadcrumbs, headings, category menus and pagination links. When the employee starts a full category capture, the content script requests same-origin category pages inside that employee\'s supplier tab one at a time at a limited rate (by default one page every 1.5 seconds), honours the site\'s retry instructions and stops on repeated refusals, sign-in pages, leaving the page or when the employee stops it. No additional browser permission is requested for category capture and cookies are never read or copied.',
        'Hot Bargain host access: connects to the trusted Hot Bargain website and API for installation-status checks, authentication, supplier configuration and authorised internal data queries.',
        'Localhost access: is available only when an employee explicitly selects the internal development option for local testing.',
      ],
    },
    {
      id: 'storage-and-retention',
      title: '5. Storage and retention',
      paragraphs: [
        'The short-lived access token and minimal account identity are held only in browser session storage and are cleared when they expire, when the employee disconnects the extension, when the browser session ends or when the Hot Bargain website session is no longer valid. The extension does not store passwords or refresh tokens. Settings are held in extension local storage until they are changed, the extension is removed or extension data is cleared. Changing the trusted API origin clears the access token, cached supplier configuration and category capture de-duplication records.',
        'Category capture de-duplication records contain only hashes of category paths and item numbers, expire after six hours and are limited to 500 entries. The progress of a running full capture is kept in session storage, and a summary of the latest full capture for each supplier (counts and the category paths completed or failed) is kept in local storage so an interrupted capture can be continued.',
        'Purchase and sales responses are used to render the requested view and are not maintained by the extension as a separate local history database. Supplier category information sent to Hot Bargain systems, Hot Bargain server records and security logs are retained under applicable business, audit, security and legal requirements.',
      ],
      items: [],
    },
    {
      id: 'sharing-and-sale',
      title: '6. Sharing, sale and advertising',
      paragraphs: [
        'Information is sent only to authorised Hot Bargain systems and to service providers that process information on our instructions where necessary to host, secure or support those systems. Access remains subject to organisational roles and store scope.',
        'We do not sell or rent personal information. We do not use extension data for advertising, creditworthiness, lending, or purposes unrelated to the extension\'s supplier-ordering function. We do not disclose it to third parties except where required to operate the service, protect people or systems, or comply with law.',
      ],
      items: [],
    },
    {
      id: 'remote-code-and-tracking',
      title: '7. Remote code and tracking',
      paragraphs: [
        'All JavaScript and modules executed by the extension are included in the installed extension package. Supplier profiles contain declarative domains, page-address patterns, selectors, numeric limits and allow-listed field transformations only; category patterns support only a simple wildcard and are never evaluated as code. The extension does not download, evaluate or execute remote JavaScript or WebAssembly.',
        'The extension does not track browsing across unrelated websites, record keystrokes or mouse activity, or create a general browsing-history record. It reads product and category information only on supported supplier origins that the employee has authorised, and records supplier category pages only, not other pages the employee visits on those sites such as search, cart, checkout or account pages. A full category capture only visits pages on the same supplier website in the employee\'s open tab and never runs without that tab.',
      ],
      items: [],
    },
    {
      id: 'security',
      title: '8. Security',
      paragraphs: [
        'We use access controls, encrypted HTTPS connections to the production Hot Bargain API, browser extension isolation and other reasonable safeguards appropriate to the information handled. No storage or transmission method can guarantee absolute security.',
        'Employees must protect their Hot Bargain credentials, use the extension only on authorised devices and supplier sites, and report suspected unauthorised access promptly.',
      ],
      items: [],
    },
    {
      id: 'choices-and-requests',
      title: '9. Employee choices and requests',
      paragraphs: [
        'An employee can revoke a supplier-site permission in the browser, switch off automatic category capture for a supplier or stop a full category capture in the side panel, disconnect the extension to remove active authentication state, clear extension data, or uninstall the extension. Revoking a permission may prevent the related supplier integration from working.',
        'For access, correction, deletion or other privacy requests concerning an employee account or internal business records, contact an authorised Hot Bargain administrator or the privacy contact below. We may need to verify identity and authority, and some records must be retained where required by law or legitimate business obligations.',
      ],
      items: [],
    },
    {
      id: 'changes-and-contact',
      title: '10. Changes, complaints and contact',
      paragraphs: [
        'We may update this policy when the extension, processing activities or legal requirements change. The latest version will be published at this public URL with a revised effective date.',
        'For questions or privacy complaints, email inquiries@hotbargain.com.au. We will investigate and respond in accordance with applicable privacy law. If you are not satisfied with our response, you may contact the Office of the Australian Information Commissioner (OAIC).',
      ],
      items: [],
    },
  ],
} satisfies BrowserExtensionPrivacyPolicy
