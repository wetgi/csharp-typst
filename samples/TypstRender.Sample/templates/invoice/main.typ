// Entry template. Reads the JSON payload, sets page styling, composes parts.
// Shared tokens come from /shared; the invoice-specific parts sit alongside.

#import "/shared/styles.typ": muted, primary
#import "parties.typ": parties-block
#import "line-items.typ": line-items-block
#import "footer.typ": footer-block

// The service injects the payload at /data.json (data-path input); the default
// keeps a local `typst compile` preview working against the demo data.json.
#let data-path = sys.inputs.at("data-path", default: "data.json")
#let inv = json(data-path)

#set page(paper: "a4", margin: 2cm)
#set text(size: 10pt)

#grid(
  columns: (1fr, auto),
  align: (left, right),
  [
    #text(size: 22pt, weight: "bold", fill: primary)[Invoice]
    #linebreak()
    #text(fill: muted)[#inv.invoiceNumber]
  ],
  [
    #text(fill: muted)[Issue date: #inv.issueDate]
    #linebreak()
    #text(fill: muted)[Due date: #inv.dueDate]
  ],
)

#v(1em)

#parties-block(inv.seller, inv.buyer)

#line-items-block(inv.lineItems, inv.currency, inv.at("discount", default: none))

#footer-block(inv.at("notes", default: none), inv.at("paidAt", default: none))
