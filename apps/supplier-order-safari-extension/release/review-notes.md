# App Review Notes

HB Supplier Order is intended for Unlisted distribution to authorised Hot Bargain employees and purchasing personnel in Australia. It does not provide public registration.

Review path:

1. Install and launch HB Supplier Order on an iPhone or iPad.
2. Open Settings → Apps → Safari → Extensions → HB Supplier Order and enable Allow Extension.
3. Open the host app to review the support and privacy links.
4. In Safari, visit https://hotbargain.vip/shop and sign in with the reviewer account supplied in App Store Connect.
5. From the signed-in HB SHOP page or the Safari toolbar, open the Supplier Order Assistant. It should recognise the website account without asking for credentials again; then select the authorised store.
6. In Safari, visit https://www.meteorparty.com.au/Party-Favors/Party-Favors-Allfavors. This supplier page does not require a supplier login.
7. Allow the extension to access the supplier site, then tap the HB Safari toolbar icon.
8. Verify the full assistant, in-page product entry, purchase records, sales records, the mutually exclusive TOP 10% / TOP 20% / TOP 30% sales bands, and the paginated supplier TOP 30% ranking. The ranking supports 60/90-day periods and page sizes of 50, 100 or 200.
9. In the supplier ranking, tap a product's total sales quantity. Verify the store breakdown shows the matching period and total, lists all enabled stores in descending quantity order (including zero-sales stores), supports store search, and returns to the same ranking item.
10. Supplier category capture (new in 1.5.0): in Safari, visit https://www.dats.com.au/office-stationery. This public supplier category page does not require a supplier login. Allow the extension to access www.dats.com.au, keep the page open for a few seconds, then open the assistant. The "Supplier categories" section shows DATS (240) with "Capture while browsing category pages" switched on; the extension has sent only the category path (Office Stationery) and the item numbers shown on that page to Hot Bargain.
11. With the DATS tab still active, tap "Capture all categories" in the assistant. Progress shows categories completed and item numbers sent while the extension reads DATS category pages one at a time in the same tab. Tap "Stop" to end it immediately. Switching away so iOS suspends the tab, or navigating the tab elsewhere, marks the capture as interrupted; return to the DATS tab and tap "Continue" to resume.
12. Last year comparison (new in 1.6.0): on a supplier product button, a "Last year" line shows this store's sales over the next 30 days starting on the same weekday last year. In the assistant, use the Last year 30 / 60 days switch; the buttons and the summary card (last order, sales to date, last year) change immediately without reloading. The line is hidden when the selected store has no sales data for that period.
13. Barcode search (new in 1.7.0): with a Bluetooth or USB scanner in keyboard mode connected, open https://www.dats.com.au and scan a product barcode. A notice at the bottom right shows the matching item number, and the page opens the supplier's search results for that item with the product outlined. Without a scanner, this feature stays inactive and nothing else changes.

The app does not automatically place supplier orders or modify supplier accounts. Category capture reads only category names, category page addresses and item numbers on supplier sites the user has allowed, never reads cookies or form contents, and requests no additional permissions. Reviewer contact details and account credentials must be entered only in App Store Connect secure fields and must not be added to this repository.
