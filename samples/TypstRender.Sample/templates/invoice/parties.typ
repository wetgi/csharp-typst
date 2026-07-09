// Seller / buyer header block. Buyer VAT row is rendered conditionally.

#import "/shared/styles.typ": muted, section-title

#let party-block(label, p) = {
  section-title(label)
  text(weight: "bold")[#p.name]
  linebreak()
  text(fill: muted)[#p.address]
  if p.at("vatId", default: none) != none {
    linebreak()
    text(fill: muted, size: 9pt)[VAT: #p.vatId]
  }
}

#let parties-block(seller, buyer) = {
  grid(
    columns: (1fr, 1fr),
    column-gutter: 1.5em,
    party-block("From", seller), party-block("Bill to", buyer),
  )
  v(1em)
}
