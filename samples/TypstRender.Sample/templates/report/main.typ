// Entry template. Showcases render-time generated assets: the C# caller draws
// the revenue chart as an SVG and ships it via TypstRenderRequest.ExtraFiles.
// Local preview uses the checked-in placeholder under report/generated; the
// generated render-time file replaces that placeholder in the uploaded bundle.

#import "/shared/styles.typ": money, muted, primary, section-title

// The service injects the payload at /data.json (data-path input); the default
// keeps the data shape readable next to this file.
#let data-path = sys.inputs.at("data-path", default: "data.json")
#let report = json(data-path)

#set page(paper: "a4", margin: 2.5cm)
#set text(size: 11pt)

#text(size: 18pt, weight: "bold", fill: primary)[#report.title]
#linebreak()
#text(fill: muted)[#report.period]

#v(1.5em)

#section-title[Summary]
#par(justify: true)[#report.summary]

#v(1.5em)

#section-title[Revenue by month]

#image("generated/chart.svg", width: 100%)

#v(1em)

#table(
  columns: (1fr, auto),
  stroke: none,
  align: (left, right),
  table.header(text(weight: "bold")[Month], text(weight: "bold")[Revenue]),
  ..report
    .chart
    .map(point => (
      point.label,
      money(point.value, report.currency),
    ))
    .flatten(),
)
