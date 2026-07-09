// Shared design tokens and helpers, reused across templates.
// Imported with a root-relative path: #import "/shared/styles.typ": ...

#let primary = rgb("#1f3a93")
#let muted = rgb("#666666")
#let rule-color = rgb("#cccccc")

#let money(amount, currency) = {
  let s = str(calc.round(amount * 100) / 100)
  if not s.contains(".") {
    s = s + ".00"
  } else {
    let parts = s.split(".")
    if parts.at(1).len() == 1 {
      s = s + "0"
    }
  }
  s + " " + currency
}

#let section-title(body) = {
  text(weight: "bold", fill: primary, size: 11pt)[#body]
  v(-0.4em)
  line(length: 100%, stroke: 0.5pt + rule-color)
  v(0.2em)
}
