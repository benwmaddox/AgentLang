# Generates html_escape vectors from the pinned Rails app's ActiveSupport.
require "json"
require "active_support"
require "active_support/core_ext/string/output_safety"

inputs = [
  "", "plain text", "&", "<", ">", "\"", "'", "<script>alert('x')</script>",
  "a & b < c > d \" e ' f", "&amp; already escaped", "&lt;&gt;&quot;&#39;",
  "café λ ü", "🚀 rocket 𐐷", "tab\there\nnewline", "`backtick` = and /slash",
  "&&&<<<>>>\"\"\"'''", "<div class=\"message\" data-id='7'>", " nbsp   ls"
]
vectors = inputs.map { |input| { "input" => input, "escaped" => ERB::Util.html_escape(input).to_str } }
File.write(ARGV.fetch(0), JSON.pretty_generate({
  "source" => "ActiveSupport #{ActiveSupport::VERSION::STRING} ERB::Util.html_escape",
  "vectors" => vectors
}) + "\n")
puts "#{vectors.length} vectors"
