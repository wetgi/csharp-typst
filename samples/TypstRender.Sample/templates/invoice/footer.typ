// Conditional notes block + optional rotated "PAID" stamp.

#import "/shared/styles.typ": muted, section-title

#let paid-stamp(paid-at) = {
  place(
    top + right,
    dx: -2cm,
    dy: 1cm,
    rotate(
      -15deg,
      box(
        stroke: 3pt + red,
        inset: (x: 12pt, y: 6pt),
        radius: 4pt,
        text(weight: "bold", fill: red, size: 22pt)[PAID],
      ),
    ),
  )
  v(0.5em)
  text(fill: muted, size: 9pt)[Paid on #paid-at.]
}

#let footer-block(notes, paid-at) = {
  if paid-at != none {
    paid-stamp(paid-at)
  }

  if notes != none {
    v(1em)
    section-title("Notes")
    text(fill: muted)[#notes]
  }
}
