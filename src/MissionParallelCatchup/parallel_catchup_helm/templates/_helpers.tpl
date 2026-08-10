{{/*
Render a list of node-label selectors as "key:value,key:value".

Accepts both shapes the chart takes elsewhere: {key, values} maps from the
mission, and plain "key:value" strings from a hand-run install.
*/}}
{{- define "catchup.labelPairs" -}}
{{- $out := list -}}
{{- range . -}}
{{- if kindIs "map" . -}}
{{- $out = append $out (printf "%s:%s" .key (first (default (list "") .values))) -}}
{{- else -}}
{{- $out = append $out (toString .) -}}
{{- end -}}
{{- end -}}
{{- join "," $out -}}
{{- end -}}
