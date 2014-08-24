import helper
from helper import Command

def start(args, logfile, errfile):
  db_host = "DB_HOST={0}".format(args.database_host or 'localhost')
  start_server = db_host + " rvm ruby-2.0.0-p0 do bundle exec puma -C config/puma.rb -w 8 --preload"

  commands = [
    Command("rvm ruby-2.0.0-p0 do bundle --jobs 4", True),
    Command(start_server, False)
  ]

  return helper.run(commands, logfile, errfile, args.troot)

def stop(logfile, errfile):
  return helper.stop('puma', logfile, errfile)
