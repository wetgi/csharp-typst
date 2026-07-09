// Entry template. Reads the JSON payload and lays out a one-page business
// letter: sender top-right, recipient, date, subject, body, sign-off.
// Reuses the shared colour tokens to stay visually consistent with the invoice.

#import "/shared/styles.typ": primary, muted

// The service injects the payload at /data.json (data-path input); the default
// keeps a local `typst compile` preview working against the demo data.json.
#let data-path = sys.inputs.at("data-path", default: "data.json")
#let letter = json(data-path)

#set page(paper: "a4", margin: 2.5cm)
#set text(size: 11pt)
#set par(justify: true)

// Static assets travel with the template: the path is relative to this file,
// so it resolves identically in local preview and in the uploaded bundle.
#align(right)[
  #image("logo.jpg", width: 3cm)
  #v(0.8em)
  #text(weight: "bold")[#letter.sender.name]
  #linebreak()
  #text(fill: muted)[#letter.sender.address]
]

#v(2em)

#text(weight: "bold")[#letter.recipient.name]
#linebreak()
#text(fill: muted)[#letter.recipient.address]

#v(1.5em)

#align(right, text(fill: muted)[#letter.date])

#v(1.5em)

#text(weight: "bold", fill: primary)[#letter.subject]

#v(1em)

#for paragraph in letter.paragraphs {
  par(paragraph)
  v(0.6em)
}

#v(1.5em)

#letter.closing

#v(3em)

#text(weight: "bold")[#letter.signatureName]
