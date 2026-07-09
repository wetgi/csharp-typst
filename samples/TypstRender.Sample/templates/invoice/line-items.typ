// Line items table + totals. A discount row only appears if `discount` is set.

#import "/shared/styles.typ": money, primary, rule-color, section-title

#let line-items-block(items, currency, discount) = {
  section-title("Items")

  let header = (
    [*Description*],
    align(right)[*Qty*],
    align(right)[*Unit price*],
    align(right)[*VAT*],
    align(right)[*Line total*],
  )

  let rows = ()
  let subtotal = 0.0
  let vat-total = 0.0
  for li in items {
    let line-net = li.quantity * li.unitPrice
    let line-vat = line-net * li.vatRate
    subtotal = subtotal + line-net
    vat-total = vat-total + line-vat
    rows.push((
      [#li.description],
      align(right)[#li.quantity],
      align(right)[#money(li.unitPrice, currency)],
      align(right)[#(str(calc.round(li.vatRate * 100)) + "%")],
      align(right)[#money(line-net + line-vat, currency)],
    ))
  }

  table(
    columns: (1fr, auto, auto, auto, auto),
    align: (left, right, right, right, right),
    stroke: 0.5pt + rule-color,
    inset: 6pt,
    ..header,
    ..rows.flatten(),
  )

  v(0.5em)

  let total = subtotal + vat-total
  if discount != none {
    total = total - discount.amount
  }

  let totals-row(label, value, emphasised: false) = (
    if emphasised { text(weight: "bold", fill: primary)[#label] } else { label },
    align(right, if emphasised { text(weight: "bold", fill: primary)[#value] } else { value }),
  )

  let totals = ()
  totals = totals + totals-row("Subtotal", money(subtotal, currency))
  totals = totals + totals-row("VAT", money(vat-total, currency))
  if discount != none {
    totals = (
      totals
        + totals-row(
          "Discount (" + discount.label + ")",
          "−" + money(discount.amount, currency),
        )
    )
  }
  totals = totals + totals-row("Total", money(total, currency), emphasised: true)

  align(right)[
    #table(
      columns: (auto, auto),
      stroke: none,
      inset: (x: 8pt, y: 3pt),
      ..totals,
    )
  ]
}
