# iOS App Review Notes

Use the following account from the standard sign-in screen:

- Username: `ios_app_review`
- Password: provided separately in App Store Connect under App Review Information

Accounts are provisioned by the employer or an authorised organisation administrator. Employees cannot register or create an account in this iOS app. The organisation manages account activation, deactivation, password reset, correction, and deletion subject to employment, payroll, tax, and other legal retention requirements. The app handles information supplied through those organisational systems, by employees when they submit, upload, or scan content, through feature-specific device permissions, and through automatic security and diagnostic logs. Employees can contact their organisation administrator or `inquiries@hotbargain.com.au` for a data access, correction, or deletion request.

The privacy policy is available before sign-in from the **Privacy** link and after sign-in from **Settings > Privacy**. The same policy is publicly available at `https://hotbargain.vip/privacy/mobile` without authentication. It explains that authorised service providers must apply equivalent safeguards, that overseas processing is limited to Singapore, and that privacy complaints are acknowledged, investigated, and normally answered in writing within 30 days; unresolved complaints may be escalated to the Office of the Australian Information Commissioner (OAIC).

This iOS production build includes a fully featured offline demo mode for App Review. The demo contains synthetic sample data only and does not connect to the production API, database, object storage, logging service, or device heartbeat service.

Before sign-in, the app makes one anonymous request to check for an app update. It contains only the app version and update identifiers, with no account or device data. No update is offered to a build under review, and the check stops as soon as the demo session starts.

The five primary navigation destinations are **Home** (the workspace), **Scan**, **Check in**, **Reports**, and **Me**. All authorised business functions are grouped by domain on **Home** and remain available according to the review account's menu permissions. Data-changing actions update an in-memory demo dataset so reviewers can create, edit, submit, approve, clock in, and view the resulting changes across screens. Demo changes reset when the app is restarted or the reviewer signs out.

Navigation follows the standard iOS hierarchy: tapping **Home** returns directly to the workspace root, while a left-edge swipe returns from secondary and tertiary screens to the previous level.

No external hardware is required:

- On Product Query, tap **Use Sample Barcode** to load barcode `9330000000017`.
- Label and receipt printing show the existing preview flow and report a simulated success.
- Attendance uses a fixed demonstration location in Brisbane.
- Image uploads retain a local preview and do not upload the selected file.
- Advertisement videos recorded inside the app are silent and limited to 30 seconds. The app does not request microphone permission. Reviewers can also select an existing photo or video from the media library; an existing video may contain audio.
- Report and data exports create local sample files.

Permission use is limited to the feature the reviewer opens:

- Camera: barcode scanning, inventory/leave/employee photos, and silent advertisement video recording.
- Photo Library: employee profile media, business attachments, and advertisement photos or videos.
- Precise and background location: attendance verification during an active shift only.

The persistent banner **App Review Demo / Local sample data / Resets on restart or sign-out** identifies the demo session throughout the app.

## Unlisted distribution

This app is intended for unlisted App Store distribution to Hot Bargain employees and authorised store managers only. It is not a consumer app and has no public self-sign-up. Accounts are provisioned by the employer or an authorised organisation administrator or store manager, including a store-scoped in-app staff creation flow; access is enforced by account, role, store and device permissions. The unlisted link will be shared only through internal or authorised organisation channels. The app can be used on employer-managed devices and authorised employee-owned devices.

Sign-in details are entered in App Review Information and are not repeated here.
